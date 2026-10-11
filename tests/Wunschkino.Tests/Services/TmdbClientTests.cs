using System.Net;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Services;

public class TmdbClientTests
{
    private const string ReadAccessToken = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiJ0ZXN0In0.c2lnbmF0dXI";

    [Fact]
    public async Task TestConnection_WithV3Key_SendsKeyAsQueryParameter()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK);
        var client = new TmdbClient(new HttpClient(handler));

        var result = await client.TestConnectionAsync(" v3key ");

        Assert.True(result.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.themoviedb.org/3/authentication?api_key=v3key", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task TestConnection_WithReadAccessToken_SendsBearerHeader()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK);
        var client = new TmdbClient(new HttpClient(handler));

        await client.TestConnectionAsync(ReadAccessToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.themoviedb.org/3/authentication", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ReadAccessToken, request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task TestConnection_RejectedKey_Fails()
    {
        var client = new TmdbClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.Unauthorized)));

        var result = await client.TestConnectionAsync("falsch");

        Assert.False(result.Success);
        Assert.Contains("abgelehnt", result.Message);
    }

    [Fact]
    public async Task TestConnection_Unreachable_Fails()
    {
        var client = new TmdbClient(new HttpClient(FakeHttpHandler.Throws(new HttpRequestException("offline"))));

        var result = await client.TestConnectionAsync("v3key");

        Assert.False(result.Success);
    }
}
