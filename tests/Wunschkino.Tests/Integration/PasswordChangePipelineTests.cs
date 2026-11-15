using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Integration;

/// <summary>Neues Konto: mit dem Admin-Passwort anmelden, eigenes Passwort setzen, mit dem neuen Passwort wieder anmelden.</summary>
public partial class PasswordChangePipelineTests : IAsyncLifetime
{
    private const string Username = "anna";
    private const string AdminSetPassword = "vom-admin-vergeben";
    private const string OwnPassword = "mein-eigenes-passwort";

    private readonly WunschkinoFactory _factory = new();
    private int _userId;

    public async Task InitializeAsync()
    {
        _factory.ExtraSettings["Wunschkino:SecureCookies"] = "false";
        await _factory.SeedAdminAsync();
        var created = await _factory.Services.GetRequiredService<UserService>().CreateAsync(1, Username, AdminSetPassword, UserRole.User);
        Assert.True(created.Success, created.Error);
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        _userId = await db.Users.Where(u => u.Username == Username).Select(u => u.Id).SingleAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<string> TokenFromAsync(HttpResponseMessage response) =>
        AntiforgeryToken().Match(await response.Content.ReadAsStringAsync()).Groups[1].Value;

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string token, string password) =>
        client.PostAsync("/login", new FormUrlEncodedContent([
            new("_handler", "login"), new("__RequestVerificationToken", token),
            new("Input.Username", Username), new("Input.Password", password)]));

    private async Task<HttpClient> SignedInWithTheAdminSetPasswordAsync()
    {
        var client = _factory.CreateNonRedirectingClient();
        var signIn = await PostLoginAsync(client, await TokenFromAsync(await client.GetAsync("/login")), AdminSetPassword);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        // Wie im Browser: Die Seite zur Passwortänderung prüft das Cookie, bevor das neue Passwort gespeichert wird.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/account/password")).StatusCode);
        return client;
    }

    private async Task ChangeOwnPasswordAsync()
    {
        var changed = await _factory.Services.GetRequiredService<UserService>().ChangeOwnPasswordAsync(_userId, AdminSetPassword, OwnPassword);
        Assert.True(changed.Success, changed.Error);
    }

    [Fact]
    public async Task AfterChangingThePassword_TheOldCookieIsRejectedAtOnce()
    {
        var client = await SignedInWithTheAdminSetPasswordAsync();

        await ChangeOwnPasswordAsync();
        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
    }

    [Fact]
    public async Task SigningInWithTheNewPassword_Works_EvenIfTheFormIsSentAfterTheAccountCacheExpired()
    {
        var client = await SignedInWithTheAdminSetPasswordAsync();
        await ChangeOwnPasswordAsync();

        var loginPage = await client.GetAsync("/login?passwordChanged=true");
        if (loginPage.StatusCode == HttpStatusCode.Redirect)
            loginPage = await client.GetAsync(loginPage.Headers.Location!);
        var token = await TokenFromAsync(loginPage);
        ((MemoryCache)_factory.Services.GetRequiredService<IMemoryCache>()).Clear();
        var signIn = await PostLoginAsync(client, token, OwnPassword);

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/wishes")).StatusCode);
    }

    [Fact]
    public async Task OpeningTheLoginPage_WhileSignedIn_EndsThatSession()
    {
        var client = await SignedInWithTheAdminSetPasswordAsync();

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AuthCookie.Name + "=;"));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/wishes")).StatusCode);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
