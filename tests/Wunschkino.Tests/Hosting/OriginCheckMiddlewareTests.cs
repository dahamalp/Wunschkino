using Microsoft.AspNetCore.Http;
using Wunschkino.Hosting;

namespace Wunschkino.Tests.Hosting;

public class OriginCheckMiddlewareTests
{
    private static DefaultHttpContext Request(string method, string path, string host = "wishes.example.org", string? origin = null, string? fetchSite = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (fetchSite is not null) context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<bool> PassesAsync(DefaultHttpContext context)
    {
        var passed = false;
        await new OriginCheckMiddleware(_ => { passed = true; return Task.CompletedTask; }).InvokeAsync(context);
        return passed;
    }

    [Theory]
    [InlineData("https://wishes.example.org")]
    [InlineData("https://WISHES.example.org")]
    [InlineData("http://wishes.example.org")]
    [InlineData("https://wishes.example.org:443")]
    public async Task TheHub_AcceptsItsOwnOrigin(string origin)
    {
        Assert.True(await PassesAsync(Request("POST", "/_blazor/negotiate", origin: origin)));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://wishes.example.org.evil.example")]
    [InlineData("https://evil.wishes.example.org")]
    [InlineData("https://wishes.example.org:8443")]
    [InlineData("null")]
    [InlineData("kein-uri")]
    public async Task TheHub_RejectsAForeignOrigin_With403(string origin)
    {
        var context = Request("POST", "/_blazor/negotiate", origin: origin);

        Assert.False(await PassesAsync(context));
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.NotEqual(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData("GET", "/_blazor")]
    [InlineData("POST", "/login")]
    [InlineData("POST", "/account/logout")]
    [InlineData("PUT", "/anything")]
    [InlineData("DELETE", "/anything")]
    public async Task HubAndStateChangingRequests_FromAForeignSite_AreRejected(string method, string path)
    {
        Assert.False(await PassesAsync(Request(method, path, origin: "https://evil.example")));
        Assert.False(await PassesAsync(Request(method, path, fetchSite: "cross-site")));
        Assert.False(await PassesAsync(Request(method, path, fetchSite: "same-site")));
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/login")]
    [InlineData("HEAD", "/")]
    [InlineData("OPTIONS", "/anything")]
    public async Task SafeRequestsToPages_AreNeverChecked(string method, string path)
    {
        Assert.True(await PassesAsync(Request(method, path, origin: "https://evil.example", fetchSite: "cross-site")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task NonBrowsersAndSameOriginRequests_PassWithoutAnOriginHeader(string? fetchSite)
    {
        Assert.True(await PassesAsync(Request("POST", "/login", fetchSite: fetchSite)));
        Assert.True(await PassesAsync(Request("POST", "/_blazor/negotiate", fetchSite: fetchSite)));
    }

    [Fact]
    public async Task TheOriginHeader_WinsOverSecFetchSite()
    {
        Assert.True(await PassesAsync(Request("POST", "/login", origin: "https://wishes.example.org", fetchSite: "cross-site")));
        Assert.False(await PassesAsync(Request("POST", "/login", origin: "https://evil.example", fetchSite: "same-origin")));
    }

    [Theory]
    [InlineData("https://wishes.example.org:8443", "127.0.0.1:8080")]
    [InlineData("https://wishes.example.org", "127.0.0.1:8080")]
    [InlineData("https://other.example.org", "127.0.0.1:8080")]
    public async Task TheConfiguredDomains_AreOwnOrigins_EvenIfTheProxyRewritesTheHostHeader(string origin, string host)
    {
        var options = new WunschkinoOptions { Domain = "wishes.example.org, other.example.org" };
        var passed = false;
        var context = Request("POST", "/_blazor/negotiate", host: host, origin: origin);

        await new OriginCheckMiddleware(_ => { passed = true; return Task.CompletedTask; }, options).InvokeAsync(context);

        Assert.True(passed);
    }

    [Fact]
    public async Task AForeignOrigin_StaysRejected_WhenDomainsAreConfigured()
    {
        var options = new WunschkinoOptions { Domain = "wishes.example.org" };
        var passed = false;
        var context = Request("POST", "/login", host: "wishes.example.org", origin: "https://evil.wishes.example.org");

        await new OriginCheckMiddleware(_ => { passed = true; return Task.CompletedTask; }, options).InvokeAsync(context);

        Assert.False(passed);
    }

    [Fact]
    public async Task APortInTheHostHeader_MustMatchTheOriginPort()
    {
        Assert.True(await PassesAsync(Request("POST", "/login", host: "localhost:5000", origin: "http://localhost:5000")));
        Assert.False(await PassesAsync(Request("POST", "/login", host: "localhost:5000", origin: "http://localhost:6000")));
    }
}
