using System.Security.Claims;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class LoginServiceTests : IDisposable
{
    private const string Password = "ein-sicheres-passwort";

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly LoginService _login;

    public LoginServiceTests() => _login = new LoginService(_db, new PlainTextHasher(), _time);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CorrectPassword_ReturnsPrincipalWithClaims()
    {
        var user = TestUsers.Add(_db, "anna", UserRole.Admin);

        var result = await _login.AuthenticateAsync("anna", Password);

        Assert.True(result.Success);
        Assert.Equal(user.Id, AuthClaims.GetUserId(result.Principal!));
        Assert.Equal("anna", result.Principal!.Identity!.Name);
        Assert.True(result.Principal.IsInRole(AuthClaims.AdminRole));
        Assert.Equal(user.SecurityStamp, result.Principal.FindFirstValue(AuthClaims.SecurityStamp));
    }

    [Fact]
    public async Task Username_IsCaseInsensitive()
    {
        TestUsers.Add(_db, "anna");

        Assert.True((await _login.AuthenticateAsync(" ANNA ", Password)).Success);
    }

    [Theory]
    [InlineData("anna", "falsches-passwort")]
    [InlineData("unbekannt", Password)]
    [InlineData("", Password)]
    [InlineData("anna", "")]
    public async Task InvalidCredentials_FailWithGenericError(string username, string password)
    {
        TestUsers.Add(_db, "anna");

        var result = await _login.AuthenticateAsync(username, password);

        Assert.False(result.Success);
        Assert.Equal(LoginService.GenericError, result.Error);
    }

    [Fact]
    public async Task DisabledAccount_CannotLogIn()
    {
        TestUsers.Add(_db, "anna", disabled: true);

        Assert.False((await _login.AuthenticateAsync("anna", Password)).Success);
    }

    // Die Kontosperre gibt es nur noch für die Passwortänderung (LoginThrottle); die Anmeldung zählt keine Fehlversuche am Konto mehr.
    private async Task LockAccountLikeAFailedPasswordChangeAsync(int userId)
    {
        await using var db = _db.CreateDbContext();
        for (var i = 0; i < LoginThrottle.MaxFailedAttempts; i++)
            await LoginThrottle.RegisterFailureAsync(db, userId, _time.GetUtcNow().UtcDateTime, CancellationToken.None);
    }

    [Fact]
    public async Task ManyWrongPasswords_NeverLockTheAccount_SoNoStrangerCanLockOutTheOwner()
    {
        var user = TestUsers.Add(_db, "anna");
        for (var i = 0; i < LoginThrottle.MaxFailedAttempts * 4; i++)
            await _login.AuthenticateAsync("anna", "falsch");

        var result = await _login.AuthenticateAsync("anna", Password);

        Assert.True(result.Success);
        Assert.Null(TestUsers.Reload(_db, user.Id).LockedUntilUtc);
    }

    [Fact]
    public async Task AccountLockedByFailedPasswordChanges_CannotLogIn()
    {
        var user = TestUsers.Add(_db, "anna");
        await LockAccountLikeAFailedPasswordChangeAsync(user.Id);

        Assert.False((await _login.AuthenticateAsync("anna", Password)).Success);
    }

    [Fact]
    public async Task Lockout_ExpiresAfterLockoutDuration()
    {
        var user = TestUsers.Add(_db, "anna");
        await LockAccountLikeAFailedPasswordChangeAsync(user.Id);

        _time.Advance(LoginThrottle.LockoutDuration + TimeSpan.FromSeconds(1));

        Assert.True((await _login.AuthenticateAsync("anna", Password)).Success);
    }

    [Fact]
    public async Task SuccessfulLogin_ResetsFailureCounter()
    {
        var user = TestUsers.Add(_db, "anna");
        await using (var db = _db.CreateDbContext())
            await LoginThrottle.RegisterFailureAsync(db, user.Id, _time.GetUtcNow().UtcDateTime, CancellationToken.None);

        await _login.AuthenticateAsync("anna", Password);

        Assert.Equal(0, TestUsers.Reload(_db, user.Id).FailedLogins);
    }

    [Fact]
    public async Task LockedAccount_StillSpendsOneHashVerification()
    {
        var hasher = new CountingHasher();
        var login = new LoginService(_db, hasher, _time);
        var user = TestUsers.Add(_db, "anna");
        await LockAccountLikeAFailedPasswordChangeAsync(user.Id);
        var verificationsBefore = hasher.Verifications;

        await login.AuthenticateAsync("anna", Password);

        Assert.Equal(verificationsBefore + 1, hasher.Verifications);
    }

    [Fact]
    public async Task OverlongPassword_IsRejectedWithoutHashing()
    {
        var hasher = new CountingHasher();
        var login = new LoginService(_db, hasher, _time);
        TestUsers.Add(_db, "anna");

        var result = await login.AuthenticateAsync("anna", new string('x', UserValidation.MaxPasswordLength + 1));

        Assert.False(result.Success);
        Assert.Equal(0, hasher.Verifications);
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        private readonly PlainTextHasher _inner = new();
        public int Verifications { get; private set; }
        public string Hash(string password) => _inner.Hash(password);
        public bool Verify(string password, string hash)
        {
            Verifications++;
            return _inner.Verify(password, hash);
        }
    }
}
