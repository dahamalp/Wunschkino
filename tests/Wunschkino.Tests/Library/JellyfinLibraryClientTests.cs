using System.Net;
using System.Text;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using static Wunschkino.Tests.Helpers.JellyfinFixtures;

namespace Wunschkino.Tests.Library;

public class JellyfinLibraryClientTests
{
    [Fact]
    public async Task GetLibrary_MapsMoviesAndSeriesWithProviderIds()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, ItemsJson(3,
            Movie("m1", " Beispielfilm ", 2020, tmdb: "603", imdb: "tt0133093"),
            Series("s1", "Beispielserie", 2015, tmdb: "1399", poster: false),
            Item("x1", "Eine Folge", "Episode")));
        var client = new JellyfinClient(new HttpClient(handler));

        var items = await client.GetLibraryAsync("http://jellyfin:8096", "key");

        Assert.Equal(2, items.Count);
        var movie = items.Single(i => i.Kind == MediaKind.Movie);
        Assert.Equal("Beispielfilm", movie.Title);
        Assert.Equal(2020, movie.Year);
        Assert.Equal("603", movie.TmdbId);
        Assert.Equal("tt0133093", movie.ImdbId);
        Assert.True(movie.HasPoster);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), movie.AddedUtc);
        var series = items.Single(i => i.Kind == MediaKind.Series);
        Assert.False(series.HasPoster);
        Assert.Null(series.ImdbId);
    }

    [Fact]
    public async Task GetLibrary_RequestsMoviesAndSeriesWithProviderIdsAndAuthHeader()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, ItemsJson(0));
        var client = new JellyfinClient(new HttpClient(handler));

        await client.GetLibraryAsync("http://jellyfin:8096/", "key");

        var request = Assert.Single(handler.Requests);
        var uri = request.RequestUri!.ToString();
        Assert.StartsWith("http://jellyfin:8096/Items?", uri);
        Assert.Contains("IncludeItemTypes=Movie,Series", uri);
        Assert.Contains("Recursive=true", uri);
        Assert.Contains("ProviderIds", uri);
        Assert.Equal("MediaBrowser Token=\"key\"", string.Join("", request.Headers.GetValues("Authorization")));
    }

    [Fact]
    public async Task GetLibrary_LoadsAllPages()
    {
        var total = JellyfinClient.PageSize + 1;
        var handler = new FakeHttpHandler(request =>
        {
            var isFirstPage = request.RequestUri!.Query.Contains("StartIndex=0&");
            var items = isFirstPage
                ? Enumerable.Range(0, JellyfinClient.PageSize).Select(i => Movie($"m{i}", $"Film {i}")).ToArray()
                : [Movie("letzter", "Letzter Film")];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ItemsJson(total, items), Encoding.UTF8, "application/json") };
        });
        var client = new JellyfinClient(new HttpClient(handler));

        var items = await client.GetLibraryAsync("http://jellyfin:8096", "key");

        Assert.Equal(total, items.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains($"StartIndex={JellyfinClient.PageSize}&", handler.Requests[1].RequestUri!.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "abgelehnt")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task GetLibrary_ErrorStatus_ThrowsReadableException(HttpStatusCode status, string expectedText)
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(status)));

        var ex = await Assert.ThrowsAsync<JellyfinException>(() => client.GetLibraryAsync("http://jellyfin:8096", "key"));

        Assert.Contains(expectedText, ex.Message);
    }

    [Fact]
    public async Task GetLibrary_UnreachableOrGarbage_ThrowsJellyfinException()
    {
        var unreachable = new JellyfinClient(new HttpClient(FakeHttpHandler.Throws(new HttpRequestException("weg"))));
        var garbage = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, "<html>Login</html>")));

        await Assert.ThrowsAsync<JellyfinException>(() => unreachable.GetLibraryAsync("http://jellyfin:8096", "key"));
        await Assert.ThrowsAsync<JellyfinException>(() => garbage.GetLibraryAsync("http://jellyfin:8096", "key"));
    }

    [Fact]
    public async Task GetPoster_ReturnsNullForNonImageResponses()
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, "{}")));

        Assert.Null(await client.GetPosterAsync("http://jellyfin:8096", "key", "m1", 300));
    }

    [Fact]
    public async Task GetPoster_ReturnsImageResponse()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } }
        });
        var client = new JellyfinClient(new HttpClient(handler));

        using var poster = await client.GetPosterAsync("http://jellyfin:8096", "key", "m1", 300);

        Assert.NotNull(poster);
        Assert.Equal("http://jellyfin:8096/Items/m1/Images/Primary?maxWidth=300&quality=90", handler.Requests[0].RequestUri!.ToString());
    }
}
