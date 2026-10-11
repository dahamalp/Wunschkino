using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Services;

public class ConnectionSettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;
    private readonly User _admin;
    private HttpStatusCode _jellyfinStatus = HttpStatusCode.OK;
    private readonly List<HttpRequestMessage> _requests = [];

    public ConnectionSettingsServiceTests()
    {
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _admin = TestUsers.Add(_db, "admin", UserRole.Admin);
        _settings.SetAsync(SettingKeys.JellyfinUrl, "http://alt:8096", isProtected: false).GetAwaiter().GetResult();
        _settings.SetAsync(SettingKeys.JellyfinApiKey, "alter-jellyfin-key", isProtected: true).GetAwaiter().GetResult();
        _settings.SetAsync(SettingKeys.TmdbApiKey, "alter-tmdb-key", isProtected: true).GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Get_ExposesOnlyWhetherKeysExist()
    {
        var view = await CreateService().GetAsync();

        Assert.Equal("http://alt:8096", view.JellyfinUrl);
        Assert.True(view.HasJellyfinApiKey);
        Assert.True(view.HasTmdbApiKey);
        Assert.Equal(ConnectionSettingsService.DefaultRefreshMinutes, view.RefreshMinutes);
    }

    private const string AdminPassword = "ein-sicheres-passwort";

    [Fact]
    public async Task Save_WithBlankKeys_OnTheSameServer_KeepsStoredKeysWithoutPassword()
    {
        var result = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://ALT:8096/jellyfin/", "", null, 30));

        Assert.True(result.Success, string.Join(" ", result.Messages));
        Assert.Equal("http://ALT:8096/jellyfin", await _settings.GetAsync(SettingKeys.JellyfinUrl));
        Assert.Equal("30", await _settings.GetAsync(SettingKeys.JellyfinRefreshMinutes));
        Assert.Equal("alter-jellyfin-key", await _settings.GetAsync(SettingKeys.JellyfinApiKey));
        var jellyfinRequest = _requests.Single(r => r.RequestUri!.Host == "alt");
        Assert.Contains("alter-jellyfin-key", string.Join("", jellyfinRequest.Headers.GetValues("Authorization")));
    }

    [Theory]
    [InlineData("http://neu:8096")]
    [InlineData("http://alt:8097")]
    [InlineData("https://alt:8096")]
    public async Task Save_WithANewAddressButNoNewKey_IsRejectedAndTheStoredKeyNeverLeaves(string url)
    {
        var result = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate(url, null, null, 15, AdminPassword));

        Assert.False(result.Success);
        Assert.Empty(_requests);
        Assert.Equal("http://alt:8096", await _settings.GetAsync(SettingKeys.JellyfinUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("falsches-passwort")]
    public async Task Save_WithANewKeyOrAddress_NeedsTheAdminPassword(string? password)
    {
        var result = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://neu:8096", "neuer-key", null, 15, password));

        Assert.False(result.Success);
        Assert.Empty(_requests);
        Assert.Equal("alter-jellyfin-key", await _settings.GetAsync(SettingKeys.JellyfinApiKey));
    }

    [Fact]
    public async Task Save_WithANewTmdbKeyOnly_NeedsTheAdminPassword()
    {
        var withoutPassword = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://alt:8096", null, "neuer-tmdb-key", 15));
        var withPassword = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://alt:8096", null, "neuer-tmdb-key", 15, AdminPassword));

        Assert.False(withoutPassword.Success);
        Assert.True(withPassword.Success);
        Assert.Equal("neuer-tmdb-key", await _settings.GetAsync(SettingKeys.TmdbApiKey));
    }

    [Theory]
    [InlineData("http://alt:8096", "http://alt:8096/jellyfin", false)]
    [InlineData("http://alt:8096", "http://ALT:8096", false)]
    [InlineData("http://alt:80", "http://alt", false)]
    [InlineData("http://alt:8096", "http://alt:8097", true)]
    [InlineData("http://alt:8096", "https://alt:8096", true)]
    [InlineData("http://alt:8096", "http://neu:8096", true)]
    [InlineData(null, "http://neu:8096", true)]
    [InlineData("kaputt", "http://neu:8096", true)]
    public void ChangesEndpoint_LooksOnlyAtSchemeHostAndPort(string? stored, string proposed, bool expected) =>
        Assert.Equal(expected, ConnectionSettingsService.ChangesEndpoint(stored, proposed));

    [Fact]
    public async Task Save_WithNewKeys_StoresThemEncrypted()
    {
        var result = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://alt:8096", "neuer-jellyfin-key", "neuer-tmdb-key", 15, AdminPassword));

        Assert.True(result.Success);
        Assert.Equal("neuer-jellyfin-key", await _settings.GetAsync(SettingKeys.JellyfinApiKey));
        Assert.Equal("neuer-tmdb-key", await _settings.GetAsync(SettingKeys.TmdbApiKey));
        using var db = _db.CreateDbContext();
        Assert.True(db.Settings.Single(s => s.Key == SettingKeys.TmdbApiKey).IsProtected);
    }

    [Fact]
    public async Task Save_WhenConnectionTestFails_ChangesNothing()
    {
        _jellyfinStatus = HttpStatusCode.Unauthorized;

        var result = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://neu:8096", "falscher-key", null, 15, AdminPassword));

        Assert.False(result.Success);
        Assert.Equal("http://alt:8096", await _settings.GetAsync(SettingKeys.JellyfinUrl));
        Assert.Equal("alter-jellyfin-key", await _settings.GetAsync(SettingKeys.JellyfinApiKey));
    }

    [Fact]
    public async Task Save_ByNonAdmin_IsRejectedWithoutRequests()
    {
        var user = TestUsers.Add(_db, "anna");

        var result = await CreateService().SaveAsync(user.Id, new ConnectionSettingsUpdate("http://neu:8096", null, null, 15));

        Assert.False(result.Success);
        Assert.Empty(_requests);
        Assert.Equal("http://alt:8096", await _settings.GetAsync(SettingKeys.JellyfinUrl));
    }

    [Theory]
    [InlineData("ftp://neu", 15)]
    [InlineData("http://user:pw@neu:8096", 15)]
    [InlineData("http://neu:8096", 1)]
    [InlineData("http://neu:8096", 5000)]
    public async Task Save_WithInvalidInput_IsRejectedWithoutRequests(string url, int refreshMinutes)
    {
        var result = await CreateService().SaveAsync(_admin.Id, new ConnectionSettingsUpdate(url, null, null, refreshMinutes));

        Assert.False(result.Success);
        Assert.Empty(_requests);
    }

    private ConnectionSettingsService CreateService()
    {
        var handler = new FakeHttpHandler(request =>
        {
            _requests.Add(request);
            var isJellyfin = request.RequestUri!.Host != "api.themoviedb.org";
            var status = isJellyfin ? _jellyfinStatus : HttpStatusCode.OK;
            var json = isJellyfin ? """{"ServerName":"Heimkino","Version":"10.11.0"}""" : "{}";
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        return new ConnectionSettingsService(
            _db, _settings, new JellyfinClient(new HttpClient(handler)), new TmdbClient(new HttpClient(handler)),
            new UserService(_db, new PlainTextHasher(), TimeProvider.System));
    }
}
