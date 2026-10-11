using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Auth;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Integration;

/// <summary>Der ganze Ablauf über HTTP: Passwort, Ticket-Cookie, Code, Anmelde-Cookie.</summary>
public partial class TwoFactorLoginPipelineTests : IAsyncLifetime
{
    private readonly WunschkinoFactory _factory = new();
    private byte[] _secret = [];

    public async Task InitializeAsync()
    {
        _factory.ExtraSettings["Wunschkino:SecureCookies"] = "false";
        await _factory.SeedAdminAsync();
        var service = _factory.Services.GetRequiredService<TwoFactorService>();
        var setup = service.CreateSetup(WunschkinoFactory.AdminUsername);
        _secret = setup.Secret;
        var result = await service.EnableAsync(1, WunschkinoFactory.AdminPassword, _secret, CodeFor(DateTimeOffset.UtcNow.AddSeconds(-30)));
        Assert.True(result.Success, result.Error);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private string CodeFor(DateTimeOffset time) => Totp.ComputeCode(_secret, Totp.TimeStep(time));

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string formName, params (string Name, string Value)[] fields)
    {
        var page = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        var token = AntiforgeryToken().Match(page).Groups[1].Value;
        var form = new List<KeyValuePair<string, string>> { new("_handler", formName), new("__RequestVerificationToken", token) };
        form.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        return await client.PostAsync(path, new FormUrlEncodedContent(form));
    }

    [Fact]
    public async Task PasswordThenCode_SignsIn()
    {
        var client = _factory.CreateNonRedirectingClient();

        var first = await PostFormAsync(client, "/login", "login",
            ("Input.Username", WunschkinoFactory.AdminUsername), ("Input.Password", WunschkinoFactory.AdminPassword));

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/login/2fa", first.Headers.Location!.AbsolutePath);
        Assert.Contains(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith(TwoFactorTicket.CookieName + "=") && c.Contains("httponly"));
        Assert.DoesNotContain(first.Headers.GetValues("Set-Cookie"), c => c.Contains(AuthCookie.Name + "="));

        var second = await PostFormAsync(client, "/login/2fa", "login-2fa", ("CodeInput.Code", CodeFor(DateTimeOffset.UtcNow)));

        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Contains(second.Headers.GetValues("Set-Cookie"), c => c.Contains(AuthCookie.Name + "="));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/wishes")).StatusCode);
    }

    [Fact]
    public async Task WrongCode_DoesNotSignIn()
    {
        var client = _factory.CreateNonRedirectingClient();
        await PostFormAsync(client, "/login", "login",
            ("Input.Username", WunschkinoFactory.AdminUsername), ("Input.Password", WunschkinoFactory.AdminPassword));

        var second = await PostFormAsync(client, "/login/2fa", "login-2fa", ("CodeInput.Code", "000000"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains(LoginService.WrongSecondFactor, WebUtility.HtmlDecode(await second.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/wishes")).StatusCode);
    }

    [Fact]
    public async Task CodePageWithoutTicket_SendsBackToThePassword()
    {
        var client = _factory.CreateNonRedirectingClient();

        var page = WebUtility.HtmlDecode(await (await client.GetAsync("/login/2fa")).Content.ReadAsStringAsync());

        Assert.DoesNotContain("name=\"CodeInput.Code\"", page);
        Assert.Contains("href=\"/login\"", page);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();
}
