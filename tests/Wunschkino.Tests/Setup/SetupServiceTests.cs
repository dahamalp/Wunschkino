using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Data;
using Wunschkino.Services;
using Wunschkino.Setup;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Setup;

public class SetupServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TempDirectory _dir = new();
    private readonly SetupTokenStore _tokenStore;
    private readonly SetupState _state = new();
    private readonly SettingsService _settings;
    private readonly SetupService _service;

    public SetupServiceTests()
    {
        _tokenStore = new SetupTokenStore(_dir.Path);
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _service = new SetupService(_db, _settings, new PlainTextHasher(), _tokenStore, _state, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task InitializeAsync_WithoutUsers_CreatesToken()
    {
        await _service.InitializeAsync();

        Assert.False(_state.IsCompleted);
        Assert.True(File.Exists(_tokenStore.TokenPath));
    }

    [Fact]
    public async Task InitializeAsync_WithExistingUser_MarksCompletedAndRemovesToken()
    {
        _tokenStore.EnsureExists();
        using (var db = _db.CreateDbContext())
        {
            db.Users.Add(new User { Username = "admin", PasswordHash = "x", Role = UserRole.Admin });
            db.SaveChanges();
        }

        await _service.InitializeAsync();

        Assert.True(_state.IsCompleted);
        Assert.False(File.Exists(_tokenStore.TokenPath));
    }

    [Fact]
    public async Task CompleteAsync_WithValidToken_CreatesAdminSettingsAndConsumesToken()
    {
        await _service.InitializeAsync();

        var result = await _service.CompleteAsync(ValidRequest(ReadToken()));

        Assert.True(result.Success, result.Error);
        Assert.True(_state.IsCompleted);
        Assert.False(File.Exists(_tokenStore.TokenPath));
        using var db = _db.CreateDbContext();
        var admin = Assert.Single(db.Users);
        Assert.Equal("admin", admin.Username);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.Equal("hashed:ein-sicheres-passwort", admin.PasswordHash);
        Assert.Equal("http://jellyfin:8096", await _settings.GetAsync(SettingKeys.JellyfinUrl));
        Assert.Equal("jellyfin-key", await _settings.GetAsync(SettingKeys.JellyfinApiKey));
        Assert.Equal("tmdb-key", await _settings.GetAsync(SettingKeys.TmdbApiKey));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task CompleteAsync_StoresTheChosenWishVisibility(bool anonymous, string expectedStored)
    {
        await _service.InitializeAsync();

        var result = await _service.CompleteAsync(ValidRequest(ReadToken()) with { AnonymousWishes = anonymous });

        Assert.True(result.Success);
        Assert.Equal(expectedStored, await _settings.GetAsync(SettingKeys.WishesAnonymous));
    }

    [Fact]
    public async Task CompleteAsync_WithoutAChoice_DefaultsToAnonymousWishes()
    {
        await _service.InitializeAsync();

        await _service.CompleteAsync(ValidRequest(ReadToken()));

        Assert.True(WishPrivacy.IsAnonymous(await _settings.GetAsync(SettingKeys.WishesAnonymous)));
    }

    [Fact]
    public async Task CompleteAsync_WithWrongToken_Fails()
    {
        await _service.InitializeAsync();

        var result = await _service.CompleteAsync(ValidRequest("falsch"));

        Assert.False(result.Success);
        Assert.False(_state.IsCompleted);
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task CompleteAsync_Twice_SecondCallFails()
    {
        await _service.InitializeAsync();
        var token = ReadToken();
        await _service.CompleteAsync(ValidRequest(token));

        var second = await _service.CompleteAsync(ValidRequest(token) with { AdminUsername = "eindringling" });

        Assert.False(second.Success);
        using var db = _db.CreateDbContext();
        Assert.Single(db.Users);
    }

    [Theory]
    [InlineData("ab", "ein-sicheres-passwort", "http://jellyfin:8096")]
    [InlineData("admin", "zu-kurz", "http://jellyfin:8096")]
    [InlineData("admin", "ein-sicheres-passwort", "ftp://jellyfin")]
    [InlineData("admin", "ein-sicheres-passwort", "jellyfin:8096")]
    [InlineData("admin", "ein-sicheres-passwort", "http://user:pass@jellyfin:8096")]
    [InlineData("admin", "ein-sicheres-passwort", "http://jellyfin:8096/?x=1")]
    public async Task CompleteAsync_WithInvalidInput_FailsWithoutConsumingToken(string username, string password, string jellyfinUrl)
    {
        await _service.InitializeAsync();
        var token = ReadToken();

        var result = await _service.CompleteAsync(ValidRequest(token) with
        {
            AdminUsername = username,
            AdminPassword = password,
            JellyfinUrl = jellyfinUrl
        });

        Assert.False(result.Success);
        Assert.True(_tokenStore.IsValid(token));
    }

    [Fact]
    public async Task CompleteAsync_RejectsApiKeysWithControlCharacters()
    {
        await _service.InitializeAsync();

        var result = await _service.CompleteAsync(ValidRequest(ReadToken()) with { JellyfinApiKey = "abc\r\nX-Injected: 1" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task IsTokenValid_IsFalseAfterCompletion()
    {
        await _service.InitializeAsync();
        var token = ReadToken();
        await _service.CompleteAsync(ValidRequest(token));

        Assert.False(_service.IsTokenValid(token));
    }

    private string ReadToken() => File.ReadAllText(_tokenStore.TokenPath);

    private static SetupRequest ValidRequest(string token) =>
        new(token, "admin", "ein-sicheres-passwort", "http://jellyfin:8096/", "jellyfin-key", "tmdb-key");
}
