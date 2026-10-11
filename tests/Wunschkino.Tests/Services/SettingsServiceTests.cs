using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;

    public SettingsServiceTests() =>
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ProtectedValue_IsEncryptedInDatabase_AndReadableViaService()
    {
        await _settings.SetAsync(SettingKeys.TmdbApiKey, "geheimer-key", isProtected: true);

        using var db = _db.CreateDbContext();
        var stored = db.Settings.Single(s => s.Key == SettingKeys.TmdbApiKey);
        Assert.True(stored.IsProtected);
        Assert.NotEqual("geheimer-key", stored.Value);
        Assert.Equal("geheimer-key", await _settings.GetAsync(SettingKeys.TmdbApiKey));
    }

    [Fact]
    public async Task UnprotectedValue_IsStoredInPlainText()
    {
        await _settings.SetAsync(SettingKeys.JellyfinUrl, "http://jellyfin:8096", isProtected: false);

        using var db = _db.CreateDbContext();
        Assert.Equal("http://jellyfin:8096", db.Settings.Single(s => s.Key == SettingKeys.JellyfinUrl).Value);
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingValue()
    {
        await _settings.SetAsync(SettingKeys.JellyfinUrl, "http://alt:8096", isProtected: false);
        await _settings.SetAsync(SettingKeys.JellyfinUrl, "http://neu:8096", isProtected: false);

        Assert.Equal("http://neu:8096", await _settings.GetAsync(SettingKeys.JellyfinUrl));
    }

    [Fact]
    public async Task ValueEncryptedWithLostKeys_ReadsAsNull()
    {
        await _settings.SetAsync(SettingKeys.TmdbApiKey, "geheimer-key", isProtected: true);
        var withNewKeys = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);

        Assert.Null(await withNewKeys.GetAsync(SettingKeys.TmdbApiKey));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownKey()
    {
        Assert.Null(await _settings.GetAsync("Unbekannt"));
    }
}
