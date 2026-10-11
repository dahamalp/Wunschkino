using System.Net;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Integration;

public class PosterProxyTests : IAsyncLifetime
{
    private readonly WunschkinoFactory _factory = new();

    public Task InitializeAsync() => _factory.SeedAdminAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/tmdb-posters/matrix1.jpg")]
    [InlineData("/anilist-covers/file/anilistcdn/media/anime/cover/medium/bx1.jpg")]
    [InlineData("/wishes")]
    [InlineData("/wishes/new")]
    public async Task WishEndpoints_RedirectAnonymousUsersToLogin(string path)
    {
        var response = await _factory.CreateNonRedirectingClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.PathAndQuery);
    }
}
