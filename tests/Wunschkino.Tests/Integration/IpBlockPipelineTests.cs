using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Hosting;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Integration;

public partial class IpBlockPipelineTests
{
    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenInput();

    [GeneratedRegex("(name|value)=\"([^\"]*)\"")]
    private static partial Regex Attribute();

    private static async Task<(HttpStatusCode Status, string Body)> LoginAsync(HttpClient client, string remoteIp, string password)
    {
        using var form = new HttpRequestMessage(HttpMethod.Get, "/login");
        form.Headers.Add("X-Test-Remote-Ip", remoteIp);
        using var page = await client.SendAsync(form);
        if (page.StatusCode != HttpStatusCode.OK)
            return (page.StatusCode, await page.Content.ReadAsStringAsync());

        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in HiddenInput().Matches(await page.Content.ReadAsStringAsync()))
        {
            var attributes = Attribute().Matches(input.Value).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
            if (attributes.TryGetValue("name", out var name))
                fields.Add(new(name, attributes.GetValueOrDefault("value", "")));
        }
        fields.Add(new("Input.Username", WunschkinoFactory.AdminUsername));
        fields.Add(new("Input.Password", password));

        using var post = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent(fields) };
        post.Headers.Add("X-Test-Remote-Ip", remoteIp);
        using var response = await client.SendAsync(post);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string remoteIp)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Remote-Ip", remoteIp);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ThreeWrongPasswords_BlockTheAddress_ForEveryPage_ButNotOtherAddresses()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        await LoginAsync(client, "203.0.113.50", "falsch");
        await LoginAsync(client, "203.0.113.50", "falsch");
        var third = await LoginAsync(client, "203.0.113.50", "falsch");

        Assert.Contains("gesperrt", third.Body);
        using var blocked = await GetAsync(client, "/login", "203.0.113.50");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.InRange(int.Parse(blocked.Headers.GetValues("Retry-After").Single()), 1, 60);
        Assert.True(blocked.Headers.Contains("Content-Security-Policy"), "Auch die Sperrantwort trägt die Security-Header.");
        using var home = await GetAsync(client, "/", "203.0.113.50");
        Assert.Equal(HttpStatusCode.TooManyRequests, home.StatusCode);

        using var other = await GetAsync(client, "/login", "203.0.113.51");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task AWrongLoginsBelowTheLimit_StillShowTheNormalErrorAndAllowTheRightPassword()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        var wrong = await LoginAsync(client, "203.0.113.60", "falsch");
        var right = await LoginAsync(client, "203.0.113.60", WunschkinoFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, wrong.Status);
        Assert.Contains("Anmeldung fehlgeschlagen", wrong.Body);
        Assert.Equal(HttpStatusCode.Redirect, right.Status);
    }

    [Fact]
    public async Task ASuccessfulLogin_LandsInTheAuditLog_WithTheClientAddress_AWrongOneDoesNot()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        await LoginAsync(client, "203.0.113.61", "falsch");
        var audit = factory.Services.GetRequiredService<AuditLog>();
        Assert.Empty(await audit.ListAsync());

        await LoginAsync(client, "203.0.113.61", WunschkinoFactory.AdminPassword);

        var entry = Assert.Single(await audit.ListAsync());
        Assert.Equal(AuditActions.Login, entry.Action);
        Assert.Equal(WunschkinoFactory.AdminUsername, entry.ActorName);
        Assert.Equal("203.0.113.61", entry.Ip);
    }

    [Fact]
    public async Task APermanentBlock_AnswersForbidden_UntilAnAdminUnblocks()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            db.IpBlocks.Add(new IpBlock { Address = "203.0.113.70", Failures = 18, IsPermanent = true, LastFailureUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using var blocked = await GetAsync(client, "/login", "203.0.113.70");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.False(blocked.Headers.Contains("Retry-After"));

        await factory.Services.GetRequiredService<IpBlockService>().UnblockAsync("203.0.113.70");
        using var afterUnblock = await GetAsync(client, "/login", "203.0.113.70");
        Assert.Equal(HttpStatusCode.OK, afterUnblock.StatusCode);
    }

    [Fact]
    public async Task AnUntrustedSenderWithForwardedFor_ShowsUpAsCriticalInTheSecurityCheck()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();
        var checks = factory.Services.GetRequiredService<SecurityCheckService>();
        Assert.DoesNotContain(await checks.RunAsync(includeBlocks: false), i => i.Severity == CheckSeverity.Critical && i.Title == "Reverse-Proxy");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
        request.Headers.Add("X-Test-Remote-Ip", "203.0.113.77");
        request.Headers.Add("X-Forwarded-For", "198.51.100.7");
        using var response = await client.SendAsync(request);

        var proxy = (await checks.RunAsync(includeBlocks: false)).Single(i => i.Title == "Reverse-Proxy");
        Assert.Equal(CheckSeverity.Critical, proxy.Severity);
        Assert.Contains("203.0.113.77", proxy.Message);
    }

    [Fact]
    public async Task ARateLimitRejection_CountsAsAFailure_SoFloodingGetsTheAddressBlocked()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        for (var i = 0; i < 13; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent([new("x", "1")]) };
            request.Headers.Add("X-Test-Remote-Ip", "203.0.113.80");
            using var response = await client.SendAsync(request);
        }

        using var blocked = await GetAsync(client, "/login", "203.0.113.80");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task TheGeneralRequestLimit_BlocksAnAddressThatFloodsAnyPage()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:RequestLimitPerMinute"] = "5";
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            using var response = await GetAsync(client, "/login", "203.0.113.90");
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses.Take(5), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.All(statuses.Skip(5), status => Assert.Equal(HttpStatusCode.TooManyRequests, status));
        using var other = await GetAsync(client, "/login", "203.0.113.91");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task RequestsTriggeredByAForeignSite_NeverGetTheVictimsAddressBlocked()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        for (var i = 0; i < 20; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = new FormUrlEncodedContent([new("x", "1")]) };
            request.Headers.Add("X-Test-Remote-Ip", "203.0.113.85");
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var response = await client.SendAsync(request);
        }

        using var own = await GetAsync(client, "/login", "203.0.113.85");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task ForeignSiteRequests_DoNotUseUpTheLimitOfTheRealUser()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:RequestLimitPerMinute"] = "5";
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        for (var i = 0; i < 12; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
            request.Headers.Add("X-Test-Remote-Ip", "203.0.113.86");
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var response = await client.SendAsync(request);
        }

        using var own = await GetAsync(client, "/login", "203.0.113.86");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task TheGeneralRequestLimit_CanBeSwitchedOff()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:RequestLimitPerMinute"] = "0";
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        for (var i = 0; i < 30; i++)
        {
            using var response = await GetAsync(client, "/login", "203.0.113.95");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheServerItself_IsNeverBlocked_NotEvenByFloodingOrFailures()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:RequestLimitPerMinute"] = "5";
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        for (var i = 0; i < 12; i++)
        {
            using var response = await GetAsync(client, "/login", "127.0.0.1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        for (var i = 0; i < 5; i++)
            await LoginAsync(client, "127.0.0.1", "falsch");

        using var after = await GetAsync(client, "/login", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ATrustedProxy_IsNeverBlocked_WhileTheClientBehindItIs()
    {
        await using var factory = new WunschkinoFactory();
        factory.ExtraSettings["Wunschkino:TrustedProxies"] = "10.0.0.5";
        factory.ExtraSettings["Wunschkino:RequestLimitPerMinute"] = "5";
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        for (var i = 0; i < 12; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
            request.Headers.Add("X-Test-Remote-Ip", "10.0.0.5");
            request.Headers.Add("X-Forwarded-For", "198.51.100.7");
            using var response = await client.SendAsync(request);
            if (i >= 5)
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        using var proxyOnly = await GetAsync(client, "/login", "10.0.0.5");
        Assert.Equal(HttpStatusCode.OK, proxyOnly.StatusCode);
    }
}
