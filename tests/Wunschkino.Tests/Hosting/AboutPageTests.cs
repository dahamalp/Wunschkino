using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Hosting;

public partial class AboutPageTests
{
    private const string TmdbNotice = "This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.";

    [GeneratedRegex("<img[^>]*src=\"([^\"]*tmdb-logo[^\"]*\\.svg)\"")]
    private static partial Regex LogoPattern();

    [Fact]
    public async Task About_IsReachableWithoutSignIn_AndCarriesTheRequiredNotices()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();

        var response = await factory.CreateNonRedirectingClient().GetAsync("/about");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TmdbNotice, html);
        Assert.Contains("GNU Affero General Public License", html);
        Assert.Contains("https://github.com/dahamalp/Wunschkino", html);
        Assert.Contains("AniList", html);
        Assert.Contains("Fribb/anime-lists", html);
        Assert.Contains("stehen nicht unter der AGPL", html);
    }

    [Fact]
    public async Task About_IsReachableEvenBeforeTheSetupIsCompleted()
    {
        await using var factory = new WunschkinoFactory();

        var client = factory.CreateNonRedirectingClient();
        var about = await client.GetAsync("/about");
        var home = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, about.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/setup", home.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task About_ShowsTheOfficialTmdbLogo_FromTheOwnOrigin_AndTheFileIsServed()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        var html = await client.GetStringAsync("/about");
        var logoPath = LogoPattern().Match(html).Groups[1].Value;

        // Der Pfad ist relativ mit Fingerabdruck (images/tmdb-logo.<hash>.svg); <base href="/"> löst ihn auf. Entscheidend: kein fremder Host.
        Assert.NotEmpty(logoPath);
        Assert.DoesNotContain("://", logoPath);
        var logo = await client.GetAsync("/" + logoPath.TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType?.MediaType);
        var svg = await logo.Content.ReadAsStringAsync();
        Assert.StartsWith("<svg", svg);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Footer_OffersTheSourceCodeAndTheAboutPage_ToEveryone_ButHidesTheVersionFromAnonymousVisitors()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();

        var html = await factory.CreateNonRedirectingClient().GetStringAsync("/about");

        Assert.Contains("Quelltext (AGPL-3.0)", html);
        Assert.Contains("href=\"/about\"", html);
        Assert.DoesNotContain("· Version", html);
    }

    [Fact]
    public async Task Footer_CarriesTheTmdbNotice_EvenForAnonymousVisitors()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();

        var login = await factory.CreateNonRedirectingClient().GetStringAsync("/login");
        var footer = login[login.IndexOf("<footer", StringComparison.Ordinal)..];

        Assert.Contains(TmdbNotice, footer);
    }

    [GeneratedRegex("<input\\b[^>]*>")]
    private static partial Regex InputTagPattern();

    /// <summary>Meldet sich wie ein Browser an (Formular mit Antiforgery-Token). Cookies ohne „Secure“, weil der Test über http läuft.</summary>
    private static async Task<HttpClient> SignedInClientAsync(WunschkinoFactory factory)
    {
        factory.ExtraSettings["Wunschkino:SecureCookies"] = "false";
        await factory.SeedAdminAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var html = await client.GetStringAsync("/login");
        var fields = new Dictionary<string, string>();
        foreach (Match tag in InputTagPattern().Matches(html))
        {
            if (!tag.Value.Contains("type=\"hidden\"", StringComparison.Ordinal))
                continue;
            var name = Regex.Match(tag.Value, "name=\"([^\"]*)\"").Groups[1].Value;
            var value = Regex.Match(tag.Value, "value=\"([^\"]*)\"").Groups[1].Value;
            fields[name] = WebUtility.HtmlDecode(value);
        }
        fields["Input.Username"] = WunschkinoFactory.AdminUsername;
        fields["Input.Password"] = WunschkinoFactory.AdminPassword;

        var response = await client.PostAsync("/login", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task Footer_ShowsTheVersion_ToSignedInUsers_Only()
    {
        await using var factory = new WunschkinoFactory();
        var signedIn = await SignedInClientAsync(factory);

        var asUser = await signedIn.GetStringAsync("/about");
        var anonymous = await factory.CreateNonRedirectingClient().GetStringAsync("/about");

        Assert.Matches($"<li[^>]*>Version {System.Text.RegularExpressions.Regex.Escape(Wunschkino.Hosting.HealthEndpoint.Version)}</li>", asUser);
        Assert.DoesNotMatch("<li[^>]*>Version ", anonymous);
    }

    [Fact]
    public async Task LoginPage_HasTheFooterWithTheSourceCodeLink()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();

        var html = await factory.CreateNonRedirectingClient().GetStringAsync("/login");

        Assert.Contains("Quelltext (AGPL-3.0)", html);
        Assert.Contains("https://github.com/dahamalp/Wunschkino", html);
        Assert.Contains("href=\"/about\"", html);
        Assert.DoesNotContain("· Version", html);
    }

    [Fact]
    public async Task ConfiguredSourceUrl_IsUsedEverywhere_ForForks()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:SourceUrl"] = "https://codeberg.org/jemand/wunschkino-fork";
        await factory.SeedAdminAsync();

        var html = await factory.CreateNonRedirectingClient().GetStringAsync("/about");

        Assert.Contains("https://codeberg.org/jemand/wunschkino-fork", html);
        Assert.DoesNotContain("https://github.com/dahamalp/Wunschkino", html);
    }

    [Theory]
    [InlineData("http://github.com/x/y")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://nutzer:passwort@github.com/x/y")]
    [InlineData("https://github.com/x y")]
    [InlineData("github.com/x/y")]
    public async Task InvalidSourceUrl_StopsTheStartWithAClearMessage(string value)
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:SourceUrl"] = value;

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Quelltext-Adresse", exception.ToString());
    }
}
