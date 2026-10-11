using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Data;
using Wunschkino.Hosting;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Hosting;

public partial class HostingPipelineTests
{
    [GeneratedRegex("nonce-([A-Za-z0-9+/=]+)")]
    private static partial Regex NoncePattern();

    [GeneratedRegex("<script type=\"importmap\"[^>]*nonce=\"([^\"]+)\"")]
    private static partial Regex ImportMapNoncePattern();

    [Fact]
    public async Task Health_IsAnonymouslyReachable_BeforeTheSetupIsCompleted()
    {
        await using var factory = new WunschkinoFactory();

        var response = await factory.CreateNonRedirectingClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("127.0.0.1", null, true)]
    [InlineData("::1", null, true)]
    [InlineData("::ffff:127.0.0.1", null, true)]
    [InlineData("192.168.1.5", "192.168.1.5", true)]
    [InlineData("192.168.1.9", "192.168.1.5", false)]
    [InlineData("203.0.113.9", "192.168.1.5", false)]
    [InlineData(null, null, false)]
    public async Task Health_RevealsTheVersionOnlyToCallersOnTheServerItself(string? remote, string? local, bool expectVersion)
    {
        await using var factory = new WunschkinoFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        if (remote is not null) request.Headers.Add("X-Test-Remote-Ip", remote);
        if (local is not null) request.Headers.Add("X-Test-Local-Ip", local);

        var response = await factory.CreateNonRedirectingClient().SendAsync(request);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectVersion, json.RootElement.TryGetProperty("version", out var version));
        if (expectVersion)
            Assert.Equal(HealthEndpoint.Version, version.GetString());
    }

    [Fact]
    public async Task Health_StaysReachableAfterTheSetup_AndRevealsNothingElse()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();

        var response = await factory.CreateNonRedirectingClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["status"], json.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("en-US,en;q=0.9", "en", "Sign in")]
    [InlineData("fr-FR", "fr", "lang=\"fr\"")]
    [InlineData("nl", "nl", "lang=\"nl\"")]
    [InlineData("it-IT,it;q=0.9", "de", "Anmelden")]
    public async Task TheLoginPage_FollowsTheBrowserLanguage_AndFallsBackToTheDefault(string acceptLanguage, string expectedLang, string expectedText)
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);

        var html = await client.GetStringAsync("/login");

        Assert.Contains($"<html lang=\"{expectedLang}\"", html);
        Assert.Contains(expectedText, html);
    }

    [Fact]
    public async Task EveryResponse_CarriesTheSecurityHeaders_EvenErrorPages()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        foreach (var path in new[] { "/login", "/health", "/gibt-es-nicht" })
        {
            var response = await client.GetAsync(path);

            Assert.True(response.Headers.Contains("Content-Security-Policy"), $"CSP fehlt bei {path}");
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.True(response.Headers.Contains("Permissions-Policy"));
            Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Opener-Policy").Single());
            Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        }
    }

    [Fact]
    public async Task Csp_AllowsOnlyTheOwnOrigin_ForEveryDirective()
    {
        await using var factory = new WunschkinoFactory();
        var response = await factory.CreateNonRedirectingClient().GetAsync("/health");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var directives = csp.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d.Split(' ').Skip(1).ToArray());

        Assert.Equal(["'self'"], directives["default-src"]);
        foreach (var (name, sources) in directives)
        {
            var foreign = sources.Where(s => s is not ("'self'" or "'none'" or "data:" or "'unsafe-inline'") && !s.StartsWith("'nonce-", StringComparison.Ordinal));
            Assert.True(!foreign.Any(), $"{name} erlaubt Fremdes: {string.Join(" ", foreign)}");
        }
        Assert.DoesNotContain("'unsafe-inline'", directives["script-src"]);
        Assert.DoesNotContain("'unsafe-eval'", directives.Values.SelectMany(s => s));
        Assert.DoesNotContain("*", directives.Values.SelectMany(s => s));
        Assert.Equal("'self' data:", string.Join(" ", directives["font-src"]));
        Assert.Equal("'self' data:", string.Join(" ", directives["img-src"]));
    }

    [Fact]
    public async Task Csp_DoesNotAllowInlineScriptsOrForeignOrigins_ExceptTheNonce()
    {
        await using var factory = new WunschkinoFactory();
        var response = await factory.CreateNonRedirectingClient().GetAsync("/health");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var scriptSrc = csp.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src"));

        Assert.DoesNotContain("'unsafe-inline'", scriptSrc);
        Assert.DoesNotContain("'unsafe-eval'", scriptSrc);
        Assert.DoesNotContain("http", scriptSrc);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.Contains("form-action 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
    }

    [Fact]
    public async Task ImportMapNonce_MatchesThePolicyOfTheSameResponse_AndChangesPerRequest()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        var first = await client.GetAsync("/login");
        var second = await client.GetAsync("/login");

        var firstNonce = NoncePattern().Match(first.Headers.GetValues("Content-Security-Policy").Single()).Groups[1].Value;
        var secondNonce = NoncePattern().Match(second.Headers.GetValues("Content-Security-Policy").Single()).Groups[1].Value;
        var html = await first.Content.ReadAsStringAsync();
        Assert.NotEmpty(firstNonce);
        // Im HTML-Attribut steht ein „+“ des Base64-Nonce als „&#x2B;“; der Browser decodiert das vor dem Vergleich mit der Policy.
        Assert.Equal(firstNonce, WebUtility.HtmlDecode(ImportMapNoncePattern().Match(html).Groups[1].Value));
        Assert.NotEqual(firstNonce, secondNonce);
    }

    [Fact]
    public async Task Page_HasNoInlineScriptsBesidesTheImportMap()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();

        var html = await factory.CreateNonRedirectingClient().GetStringAsync("/login");

        var inlineScripts = Regex.Matches(html, "<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase).Select(m => m.Value).ToList();
        Assert.All(inlineScripts, tag => Assert.Contains("importmap", tag));
        Assert.DoesNotContain(" onclick=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithADomainConfigured_OtherHostHeadersAreRejected_ButLocalhostKeepsWorking()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:Domain"] = "wunsch.example.org";
        var client = factory.CreateNonRedirectingClient();

        var foreign = new HttpRequestMessage(HttpMethod.Get, "/health") { Headers = { Host = "evil.example" } };
        var configured = new HttpRequestMessage(HttpMethod.Get, "/health") { Headers = { Host = "wunsch.example.org" } };
        var local = new HttpRequestMessage(HttpMethod.Get, "/health") { Headers = { Host = "localhost" } };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(configured)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(local)).StatusCode);
    }

    [Fact]
    public async Task WithoutADomain_AnyHostIsAccepted()
    {
        await using var factory = new WunschkinoFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health") { Headers = { Host = "irgendwas.example" } };

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateNonRedirectingClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task InvalidProxyConfiguration_StopsTheStartWithAClearMessage()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:TrustedProxies"] = "kein-ip";

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Proxy-Adresse", exception.ToString());
    }

    private static async Task<List<HttpStatusCode>> PostLoginAttemptsAsync(
        HttpClient client, string remoteIp, IEnumerable<string> forwardedForPerAttempt)
    {
        var statuses = new List<HttpStatusCode>();
        foreach (var forwardedFor in forwardedForPerAttempt)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent([new("x", "1")]) };
            request.Headers.Add("X-Test-Remote-Ip", remoteIp);
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }
        return statuses;
    }

    [Fact]
    public async Task LoginLimit_CountsTheForwardedClient_WhenTheProxyIsTrusted()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:TrustedProxies"] = "10.0.0.5";
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        var differentClients = await PostLoginAttemptsAsync(client, "10.0.0.5", Enumerable.Range(1, 15).Select(i => $"203.0.113.{i}"));
        var oneClient = await PostLoginAttemptsAsync(client, "10.0.0.5", Enumerable.Repeat("198.51.100.7", 12));

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, differentClients);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, oneClient.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, oneClient[^1]);
    }

    [Fact]
    public async Task LoginLimit_IgnoresForgedForwardedHeaders_FromAnUntrustedSender()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:TrustedProxies"] = "10.0.0.5";
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        var statuses = await PostLoginAttemptsAsync(client, "203.0.113.9", Enumerable.Range(1, 12).Select(i => $"198.51.100.{i}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task LoginLimit_TreatsAnIpv6PrefixAsOneClient()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 12; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent([new("x", "1")]) };
            request.Headers.Add("X-Test-Remote-Ip", $"2001:db8:1:2::{i:x}");
            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task SecurityHeaders_SurviveTheExceptionHandlersResponseReset()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseExceptionHandler(handler => handler.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return context.Response.WriteAsync("Fehler");
        }));
        app.MapGet("/boom", (Func<string>)(() => throw new InvalidOperationException("boom")));
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Health_ReportsUnhealthy_WhenTheDatabaseHasNoSchema()
    {
        using var directory = new TempDirectory();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(directory.Path, "leer.db")};Pooling=False").Options;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDbContextFactory<AppDbContext>>(new UnmigratedDbFactory(options));
        await using var app = builder.Build();
        app.MapHealth();
        await app.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unhealthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_ReportsUnhealthy_WhenTheDatabaseIsUnreachable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDbContextFactory<AppDbContext>>(new ThrowingDbFactory());
        await using var app = builder.Build();
        app.MapHealth();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class UnmigratedDbFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("Datenbank nicht erreichbar");
    }
}
