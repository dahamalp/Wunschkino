using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Hosting;
using Wunschkino.Localization;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class SessionHardeningTests : IDisposable
{
    private const string Password = "ein-sicheres-passwort";

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private PrincipalValidator Validator(int maxDays = 30, IMemoryCache? cache = null) =>
        new(_db, new WunschkinoOptions { SessionMaxDays = maxDays }, _time, cache);

    [Fact]
    public async Task ASessionOlderThanTheMaximum_IsRejected_EvenIfEverythingElseMatches()
    {
        var user = TestUsers.Add(_db, "anna");
        var principal = AuthClaims.CreatePrincipal(user, _time.GetUtcNow());
        var validator = Validator(maxDays: 7);

        Assert.True(await validator.IsValidAsync(principal));
        _time.Advance(TimeSpan.FromDays(6));
        Assert.True(await validator.IsValidAsync(principal));
        _time.Advance(TimeSpan.FromDays(2));
        Assert.False(await validator.IsValidAsync(principal));
    }

    [Fact]
    public async Task ACookieWithoutTheIssueTime_OrFromTheFuture_IsRejected()
    {
        var user = TestUsers.Add(_db, "anna");
        var identity = new ClaimsIdentity(AuthClaims.CreatePrincipal(user, _time.GetUtcNow()).Claims.Where(c => c.Type != AuthClaims.IssuedAt), "test");

        Assert.False(await Validator().IsValidAsync(new ClaimsPrincipal(identity)));
        Assert.False(await Validator().IsValidAsync(AuthClaims.CreatePrincipal(user, _time.GetUtcNow().AddDays(2))));
    }

    [Fact]
    public async Task TheAccountLookup_IsKeptBriefly_SoOnlyTheFirstRequestAsksTheDatabase()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var user = TestUsers.Add(_db, "anna");
        var principal = AuthClaims.CreatePrincipal(user, _time.GetUtcNow());
        var validator = Validator(cache: cache);
        Assert.True(await validator.IsValidAsync(principal));

        await using (var db = _db.CreateDbContext())
        {
            db.Users.Single(u => u.Id == user.Id).SecurityStamp = User.NewSecurityStamp();
            await db.SaveChangesAsync();
        }

        Assert.True(await validator.IsValidAsync(principal));
        Assert.False(await Validator().IsValidAsync(principal));
    }

    [Fact]
    public async Task SigningInAgainRightAfterSigningOut_IsNotRejectedByTheCache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var validator = Validator(cache: cache);
        var users = new UserService(_db, new PlainTextHasher(), _time);
        var user = TestUsers.Add(_db, "anna");
        var before = AuthClaims.CreatePrincipal(user, _time.GetUtcNow());
        Assert.True(await validator.IsValidAsync(before));

        await users.RevokeSessionsAsync(user.Id);
        var after = AuthClaims.CreatePrincipal(TestUsers.Reload(_db, user.Id), _time.GetUtcNow());

        Assert.True(await validator.IsValidAsync(after));
    }

    [Fact]
    public async Task RevokingTheSessions_InvalidatesEveryExistingCookie()
    {
        var users = new UserService(_db, new PlainTextHasher(), _time);
        var user = TestUsers.Add(_db, "anna");
        var principal = AuthClaims.CreatePrincipal(user, _time.GetUtcNow());

        await users.RevokeSessionsAsync(user.Id);

        Assert.False(await Validator().IsValidAsync(principal));
    }

    [Fact]
    public async Task AdminGuard_AcceptsOnlyActiveAdmins()
    {
        var guard = new AdminGuard(_db);
        var admin = TestUsers.Add(_db, "chef", UserRole.Admin);
        var user = TestUsers.Add(_db, "anna");
        var disabledAdmin = TestUsers.Add(_db, "weg", UserRole.Admin, disabled: true);

        Assert.True(await guard.IsAdminAsync(AuthClaims.CreatePrincipal(admin)));
        Assert.False(await guard.IsAdminAsync(AuthClaims.CreatePrincipal(user)));
        Assert.False(await guard.IsAdminAsync(AuthClaims.CreatePrincipal(disabledAdmin)));
        Assert.False(await guard.IsAdminAsync(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task AnAdminSetPassword_MustBeChanged_UntilTheOwnerPicksOne()
    {
        var users = new UserService(_db, new PlainTextHasher(), _time);
        var admin = TestUsers.Add(_db, "chef", UserRole.Admin);

        await users.CreateAsync(admin.Id, "anna", Password, UserRole.User);
        User anna;
        await using (var db = _db.CreateDbContext())
            anna = db.Users.Single(u => u.Username == "anna");
        Assert.True(anna.MustChangePassword);
        Assert.True(AuthClaims.MustChangePasswordNow(AuthClaims.CreatePrincipal(anna)));

        Assert.True((await users.ChangeOwnPasswordAsync(anna.Id, Password, "neues-langes-passwort")).Success);
        Assert.False(TestUsers.Reload(_db, anna.Id).MustChangePassword);

        await users.ResetPasswordAsync(admin.Id, anna.Id, "wieder-ein-passwort");
        Assert.True(TestUsers.Reload(_db, anna.Id).MustChangePassword);
    }

    [Theory]
    [InlineData("Europe/Berlin", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("UTC", true)]
    [InlineData("Etc/GMT+5", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("Europe/../Berlin", false)]
    [InlineData("/etc/localtime", false)]
    [InlineData("Europe/Berlin\n", false)]
    [InlineData("A/B/C/D", false)]
    [InlineData("Europe\\Berlin", false)]
    [InlineData("", false)]
    public void TheBrowserTimeZone_MustLookLikeAnIanaId(string id, bool wellFormed)
    {
        var accepted = new UserTimeZone().TrySet(id);

        Assert.Equal(wellFormed && TimeZoneExists(id), accepted);
    }

    private static bool TimeZoneExists(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static DefaultHttpContext Page(string path, string method = "GET", string accept = "text/html,application/xhtml+xml", bool mustChange = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Accept = accept;
        var user = new User { Id = 1, Username = "anna", PasswordHash = "x", MustChangePassword = mustChange };
        context.User = AuthClaims.CreatePrincipal(user);
        return context;
    }

    private static async Task<bool> RedirectsAsync(DefaultHttpContext context)
    {
        var passed = false;
        await new PasswordChangeMiddleware(_ => { passed = true; return Task.CompletedTask; }).InvokeAsync(context);
        return !passed && context.Response.StatusCode == StatusCodes.Status302Found
               && context.Response.Headers.Location == PasswordChangeMiddleware.PasswordPath;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/wishes")]
    [InlineData("/admin/users")]
    public async Task PageVisits_OfAnAccountWithAnAdminSetPassword_GoToThePasswordPage(string path) =>
        Assert.True(await RedirectsAsync(Page(path)));

    [Theory]
    [InlineData("/account/password", "GET", "text/html", true)]
    [InlineData("/account/logout", "GET", "text/html", true)]
    [InlineData("/login", "GET", "text/html", true)]
    [InlineData("/_blazor", "GET", "text/html", true)]
    [InlineData("/app.css", "GET", "text/css", true)]
    [InlineData("/posters/abc", "GET", "image/avif,image/webp", true)]
    [InlineData("/wishes", "POST", "text/html", true)]
    [InlineData("/wishes", "GET", "text/html", false)]
    public async Task EverythingElse_IsLeftAlone(string path, string method, string accept, bool mustChange) =>
        Assert.False(await RedirectsAsync(Page(path, method, accept, mustChange)));
}
