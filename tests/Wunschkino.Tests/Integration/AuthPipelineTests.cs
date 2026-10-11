using System.Net;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Integration;

public class AuthPipelineTests : IAsyncLifetime
{
    private readonly WunschkinoFactory _factory = new();

    public Task InitializeAsync() => _factory.SeedAdminAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/")]
    [InlineData("/admin/users")]
    [InlineData("/account/password")]
    [InlineData("/admin/settings")]
    [InlineData("/admin/blocks")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/security")]
    [InlineData("/admin/gaps")]
    [InlineData("/posters/irgendwas")]
    public async Task ProtectedPages_RedirectAnonymousUsersToLogin(string path)
    {
        var response = await _factory.CreateNonRedirectingClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task LoginPage_IsReachableAnonymously()
    {
        var response = await _factory.CreateNonRedirectingClient().GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    public async Task TheHub_RejectsANegotiateFromAForeignOrigin(string origin)
    {
        var client = _factory.CreateNonRedirectingClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheHub_RejectsARequestTheBrowserMarksAsCrossSite()
    {
        var client = _factory.CreateNonRedirectingClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        request.Headers.Add("Sec-Fetch-Site", "same-site");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheLoginPost_FromAForeignOrigin_IsRejectedBeforeAnyPasswordIsChecked()
    {
        var client = _factory.CreateNonRedirectingClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent([new("Input.Username", WunschkinoFactory.AdminUsername), new("Input.Password", WunschkinoFactory.AdminPassword)])
        };
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_IsRejected()
    {
        var response = await _factory.CreateNonRedirectingClient().PostAsync("/account/logout", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginPosts_AreRateLimitedPerClient()
    {
        var client = _factory.CreateNonRedirectingClient();
        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            using var response = await client.PostAsync("/login", new FormUrlEncodedContent([new("x", "1")]));
            statusCodes.Add(response.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statusCodes.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, statusCodes[^1]);
    }

    [Fact]
    public async Task SetupPage_RedirectsHomeOnceSetupIsCompleted()
    {
        var response = await _factory.CreateNonRedirectingClient().GetAsync("/setup");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }
}
