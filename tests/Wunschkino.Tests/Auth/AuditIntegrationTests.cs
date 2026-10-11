using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Services;
using Wunschkino.Setup;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

/// <summary>Prüft an jeder Stelle, die ins Protokoll schreibt, dass genau das Richtige drinsteht und nie ein Geheimnis.</summary>
public class AuditIntegrationTests : IDisposable
{
    private const string Password = "ein-sicheres-passwort";
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Server = IPAddress.Parse("192.168.1.5");

    private readonly TestDb _db = new();
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly AuditLog _audit;
    private readonly UserService _users;
    private readonly User _admin;

    public AuditIntegrationTests()
    {
        _audit = new AuditLog(_db, _time, NullLogger<AuditLog>.Instance);
        _users = new UserService(_db, new PlainTextHasher(), _time, _audit);
        _admin = TestUsers.Add(_db, "admin", UserRole.Admin);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private async Task<IReadOnlyList<AuditEntry>> EntriesAsync() => await _audit.ListAsync();

    private static void AssertNoSecrets(IEnumerable<AuditEntry> entries, params string[] secrets)
    {
        foreach (var entry in entries)
        {
            var text = string.Join("|", entry.Action, entry.ActorName, entry.Target, entry.Detail, entry.Ip);
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public async Task CreatingAUser_IsLogged_WithActorTargetAndRole_ButNeverThePassword()
    {
        await _users.CreateAsync(_admin.Id, "anna", Password, UserRole.User);

        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(AuditActions.UserCreated, entry.Action);
        Assert.Equal(_admin.Id, entry.ActorUserId);
        Assert.Equal("admin", entry.ActorName);
        Assert.Equal("anna", entry.Target);
        Assert.Equal("User", entry.Detail);
        AssertNoSecrets([entry], Password);
    }

    [Fact]
    public async Task ChangingARole_IsLogged_WithTheNewRole()
    {
        var anna = TestUsers.Add(_db, "anna");

        await _users.SetRoleAsync(_admin.Id, anna.Id, UserRole.Admin);

        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(AuditActions.UserRole, entry.Action);
        Assert.Equal("anna", entry.Target);
        Assert.Equal("Admin", entry.Detail);
    }

    [Fact]
    public async Task DisablingAndEnablingAnAccount_AreLoggedAsDifferentActions()
    {
        var anna = TestUsers.Add(_db, "anna");

        await _users.SetDisabledAsync(_admin.Id, anna.Id, true);
        await _users.SetDisabledAsync(_admin.Id, anna.Id, false);

        var actions = (await EntriesAsync()).Select(e => e.Action).ToList();
        Assert.Equal([AuditActions.UserEnabled, AuditActions.UserDisabled], actions);
    }

    [Fact]
    public async Task ResettingAPassword_IsLogged_WithoutTheNewPassword()
    {
        var anna = TestUsers.Add(_db, "anna");

        await _users.ResetPasswordAsync(_admin.Id, anna.Id, "ganz-neues-passwort-123");

        var entries = await EntriesAsync();
        Assert.Equal(AuditActions.UserPasswordReset, Assert.Single(entries).Action);
        AssertNoSecrets(entries, "ganz-neues-passwort-123", Password);
    }

    [Fact]
    public async Task UnlockingAnAccount_IsLogged()
    {
        var anna = TestUsers.Add(_db, "anna");

        await _users.UnlockAsync(_admin.Id, anna.Id);

        Assert.Equal(AuditActions.UserUnlocked, Assert.Single(await EntriesAsync()).Action);
    }

    [Fact]
    public async Task ChangingTheOwnPassword_IsLoggedWithTheUserAsActor_AWrongCurrentPasswordIsNot()
    {
        var anna = TestUsers.Add(_db, "anna");

        await _users.ChangeOwnPasswordAsync(anna.Id, "falsches-passwort", "neues-passwort-12345");
        Assert.Empty(await EntriesAsync());

        await _users.ChangeOwnPasswordAsync(anna.Id, Password, "neues-passwort-12345");

        var entries = await EntriesAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(AuditActions.PasswordChanged, entry.Action);
        Assert.Equal("anna", entry.ActorName);
        AssertNoSecrets(entries, Password, "neues-passwort-12345", "falsches-passwort");
    }

    [Fact]
    public async Task RefusedOrFailedActions_LeaveNoEntry()
    {
        var normal = TestUsers.Add(_db, "bob");
        var anna = TestUsers.Add(_db, "anna");

        await _users.CreateAsync(normal.Id, "neu", Password, UserRole.User);
        await _users.SetRoleAsync(normal.Id, anna.Id, UserRole.Admin);
        await _users.CreateAsync(_admin.Id, "ab", Password, UserRole.User);
        await _users.SetDisabledAsync(_admin.Id, _admin.Id, true);

        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task ASuccessfulLogin_IsLogged_WithNameAndAddress_AFailedOneIsNot()
    {
        TestUsers.Add(_db, "anna");
        var blocks = new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance, _audit);
        var guard = new LoginGuard(new LoginService(_db, new PlainTextHasher(), _time), blocks, _time, NullLogger<LoginGuard>.Instance, _audit);

        await guard.AttemptAsync("anna", "falsch", Client, Server);
        Assert.Empty(await EntriesAsync());

        await guard.AttemptAsync("anna", Password, IPAddress.Parse("::ffff:203.0.113.7"), Server);

        var entries = await EntriesAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(AuditActions.Login, entry.Action);
        Assert.Equal("anna", entry.ActorName);
        Assert.Equal("203.0.113.7", entry.Ip);
        AssertNoSecrets(entries, Password, "falsch");
    }

    [Fact]
    public async Task ABlock_IsLogged_WithTheCountAndTheEnd_AndAnUnblockWithWhoDidIt()
    {
        var blocks = new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance, _audit);
        for (var i = 0; i < 3; i++)
            await blocks.RegisterFailureAsync(Client, Server);

        var blocked = Assert.Single(await EntriesAsync());
        Assert.Equal(AuditActions.IpBlocked, blocked.Action);
        Assert.Equal("203.0.113.7", blocked.Target);
        Assert.Contains("3 Fehlversuchen", blocked.Detail);
        Assert.Contains("UTC", blocked.Detail);

        await blocks.UnblockAsync("203.0.113.7", "admin");

        var unblocked = (await EntriesAsync())[0];
        Assert.Equal(AuditActions.IpUnblocked, unblocked.Action);
        Assert.Equal("admin", unblocked.ActorName);
        Assert.Equal("203.0.113.7", unblocked.Target);
    }

    [Fact]
    public async Task UnblockingEverything_IsLoggedWithTheCount_AndUnblockingNothingIsNot()
    {
        var blocks = new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance, _audit);
        await blocks.UnblockAsync("198.51.100.1", "admin");
        await blocks.UnblockAllAsync("admin");
        Assert.Empty(await EntriesAsync());

        for (var i = 0; i < 3; i++)
            await blocks.RegisterFailureAsync(Client, Server);
        await blocks.UnblockAllAsync("Konsole");

        var entry = (await EntriesAsync())[0];
        Assert.Equal(AuditActions.IpUnblockedAll, entry.Action);
        Assert.Equal("Konsole", entry.ActorName);
        Assert.Equal("1 Adressen", entry.Detail);
    }

    private const string AdminPassword = "ein-sicheres-passwort";

    private ConnectionSettingsService CreateConnectionSettings(out SettingsService settings, bool jellyfinWorks = true)
    {
        settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        settings.SetAsync(SettingKeys.JellyfinUrl, "http://alt:8096", isProtected: false).GetAwaiter().GetResult();
        settings.SetAsync(SettingKeys.JellyfinApiKey, "alter-jellyfin-key", isProtected: true).GetAwaiter().GetResult();
        settings.SetAsync(SettingKeys.TmdbApiKey, "alter-tmdb-key", isProtected: true).GetAwaiter().GetResult();
        var handler = new FakeHttpHandler(request =>
        {
            var isJellyfin = request.RequestUri!.Host != "api.themoviedb.org";
            var status = isJellyfin && !jellyfinWorks ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            var json = isJellyfin ? """{"ServerName":"Heimkino","Version":"10.11.0"}""" : "{}";
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        return new ConnectionSettingsService(_db, settings, new JellyfinClient(new HttpClient(handler)), new TmdbClient(new HttpClient(handler)), _users, _audit);
    }

    [Fact]
    public async Task ChangedConnectionSettings_AreLogged_NamingWhatChanged_ButNeverTheKeys()
    {
        var service = CreateConnectionSettings(out _);

        await service.SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://neu:8096", "geheimer-jellyfin-key", "geheimer-tmdb-key", 30, AdminPassword));

        var entries = await EntriesAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(AuditActions.ConnectionsChanged, entry.Action);
        Assert.Equal("admin", entry.ActorName);
        Assert.Contains("Jellyfin-Adresse", entry.Detail);
        Assert.Contains("Jellyfin-Key", entry.Detail);
        Assert.Contains("TMDB-Key", entry.Detail);
        Assert.Contains("30 Minuten", entry.Detail);
        AssertNoSecrets(entries, "geheimer-jellyfin-key", "geheimer-tmdb-key", "alter-jellyfin-key", "alter-tmdb-key");
    }

    [Fact]
    public async Task SavingUnchangedConnectionSettings_LeavesNoEntry()
    {
        var service = CreateConnectionSettings(out _);

        await service.SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://alt:8096", null, null, ConnectionSettingsService.DefaultRefreshMinutes));

        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task ARejectedConnectionSave_LeavesNoEntry()
    {
        var service = CreateConnectionSettings(out _, jellyfinWorks: false);

        var result = await service.SaveAsync(_admin.Id, new ConnectionSettingsUpdate("http://neu:8096", "neuer-key", null, 15, AdminPassword));

        Assert.False(result.Success);
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task CompletingTheSetup_IsLogged_WithTheNewAdminAsActor()
    {
        using var freshDb = new TestDb();
        var audit = new AuditLog(freshDb, _time, NullLogger<AuditLog>.Instance);
        var tokenStore = new SetupTokenStore(_dir.Path);
        var settings = new SettingsService(freshDb, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        var setup = new SetupService(freshDb, settings, new PlainTextHasher(), tokenStore, new SetupState(), _time, audit);
        await setup.InitializeAsync();
        var token = File.ReadAllText(tokenStore.TokenPath).Trim();

        var result = await setup.CompleteAsync(new SetupRequest(token, "chefin", Password, "http://jellyfin:8096", "jellyfin-key", "tmdb-key"));

        Assert.True(result.Success, result.Error);
        var entries = await audit.ListAsync();
        var entry = Assert.Single(entries);
        Assert.Equal(AuditActions.SetupCompleted, entry.Action);
        Assert.Equal("chefin", entry.ActorName);
        Assert.NotNull(entry.ActorUserId);
        AssertNoSecrets(entries, Password, "jellyfin-key", "tmdb-key", token);
    }
}
