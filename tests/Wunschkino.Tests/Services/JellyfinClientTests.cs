using System.Net;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Services;

public class JellyfinClientTests
{
    [Fact]
    public async Task TestConnection_Success_ReportsServerAndSendsTokenHeader()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, """{"ServerName":"Heimkino","Version":"10.11.0"}""");
        var client = new JellyfinClient(new HttpClient(handler));

        var result = await client.TestConnectionAsync("http://jellyfin:8096/", "abc123");

        Assert.True(result.Success);
        Assert.Contains("Heimkino", result.Message);
        Assert.Equal(["http://jellyfin:8096/System/Info", "http://jellyfin:8096/Library/VirtualFolders"],
            handler.Requests.Select(r => r.RequestUri!.ToString()));
        Assert.All(handler.Requests, r => Assert.Equal("MediaBrowser Token=\"abc123\"", string.Join("", r.Headers.GetValues("Authorization"))));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TestConnection_KeyWithoutAdminRights_FailsWithAHint(HttpStatusCode librariesStatus)
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal)
            ? new HttpResponseMessage(librariesStatus)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ServerName":"Heimkino","Version":"10.11.0"}""", System.Text.Encoding.UTF8, "application/json") });
        var client = new JellyfinClient(new HttpClient(handler));

        var result = await client.TestConnectionAsync("http://jellyfin:8096", "benutzer-token");

        Assert.False(result.Success);
        Assert.Contains("Admin-Rechte", result.Message);
    }

    [Fact]
    public async Task TestConnection_KeepsBasePathOfReverseProxySetups()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, """{"ServerName":"x","Version":"1"}""");
        var client = new JellyfinClient(new HttpClient(handler));

        await client.TestConnectionAsync("https://example.org/jellyfin", "abc123");

        Assert.Equal("https://example.org/jellyfin/System/Info", handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TestConnection_RejectedKey_Fails(HttpStatusCode status)
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(status)));

        var result = await client.TestConnectionAsync("http://jellyfin:8096", "falsch");

        Assert.False(result.Success);
        Assert.Contains("abgelehnt", result.Message);
    }

    [Fact]
    public async Task TestConnection_Unreachable_Fails()
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Throws(new HttpRequestException("kein Host"))));

        var result = await client.TestConnectionAsync("http://jellyfin:8096", "abc123");

        Assert.False(result.Success);
        Assert.Contains("nicht erreichbar", result.Message);
    }

    [Fact]
    public async Task TestConnection_KeyWithControlCharacters_IsRejectedWithoutRequest()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK);
        var client = new JellyfinClient(new HttpClient(handler));

        var result = await client.TestConnectionAsync("http://jellyfin:8096", "abc\r\nX-Injected: 1");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TestConnection_RedirectResponse_IsNotTreatedAsSuccess()
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.Found)));

        var result = await client.TestConnectionAsync("http://jellyfin:8096", "abc123");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task TestConnection_NonJsonResponse_Fails()
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, "<html>Router-Login</html>")));

        var result = await client.TestConnectionAsync("http://192.168.1.1", "abc123");

        Assert.False(result.Success);
    }
}
