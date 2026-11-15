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
        var handler = new FakeHttpHandler(WithOneLibrary(_ => Json(ItemsJson(3,
            Movie("m1", " Beispielfilm ", 2020, tmdb: "603", imdb: "tt0133093"),
            Series("s1", "Beispielserie", 2015, tmdb: "1399", poster: false),
            Item("x1", "Eine Folge", "Episode")))));
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
        var handler = new FakeHttpHandler(WithOneLibrary(_ => Json(ItemsJson(0))));
        var client = new JellyfinClient(new HttpClient(handler));

        await client.GetLibraryAsync("http://jellyfin:8096/", "key");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://jellyfin:8096/Library/VirtualFolders", handler.Requests[0].RequestUri!.ToString());
        var request = handler.Requests[1];
        var uri = request.RequestUri!.ToString();
        Assert.StartsWith("http://jellyfin:8096/Items?", uri);
        Assert.Contains($"ParentId={LibraryId}&", uri);
        Assert.Contains("IncludeItemTypes=Movie,Series", uri);
        Assert.Contains("Recursive=true", uri);
        Assert.Contains("ProviderIds", uri);
        Assert.All(handler.Requests, r => Assert.Equal("MediaBrowser Token=\"key\"", string.Join("", r.Headers.GetValues("Authorization"))));
    }

    [Fact]
    public async Task GetLibrary_LoadsAllPages()
    {
        var total = JellyfinClient.PageSize + 1;
        var handler = new FakeHttpHandler(WithOneLibrary(request =>
        {
            var isFirstPage = request.RequestUri!.Query.Contains("StartIndex=0&");
            var items = isFirstPage
                ? Enumerable.Range(0, JellyfinClient.PageSize).Select(i => Movie($"m{i}", $"Film {i}")).ToArray()
                : [Movie("letzter", "Letzter Film")];
            return Json(ItemsJson(total, items));
        }));
        var client = new JellyfinClient(new HttpClient(handler));

        var items = await client.GetLibraryAsync("http://jellyfin:8096", "key");

        Assert.Equal(total, items.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains($"StartIndex={JellyfinClient.PageSize}&", handler.Requests[2].RequestUri!.Query);
    }

    // Jellyfin kann Einträge einer gelöschten oder umbenannten Bibliothek in der Datenbank behalten. Die eigene Oberfläche zeigt
    // sie nicht mehr, eine Abfrage quer über alle Einträge liefert sie aber weiter.
    [Fact]
    public async Task GetLibrary_IgnoresLeftoversOutsideTheConfiguredLibraries_AndListsEachTitleOnce()
    {
        var handler = new FakeHttpHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal))
                return Json(VirtualFoldersJson("filme", "serien", "gemischt"));
            if (uri.Query.Contains("ParentId=filme&"))
                return Json(ItemsJson(1, Movie("m1", "Ant-Man and the Wasp", tmdb: "363088")));
            if (uri.Query.Contains("ParentId=serien&"))
                return Json(ItemsJson(1, Series("s1", "Beispielserie")));
            if (uri.Query.Contains("ParentId=gemischt&"))
                return Json(ItemsJson(1, Movie("m1", "Ant-Man and the Wasp", tmdb: "363088")));
            return Json(ItemsJson(3, Movie("m1", "Ant-Man and the Wasp", tmdb: "363088"), Series("s1", "Beispielserie"),
                Movie("rest", "Ant-Man and the Wasp", tmdb: "363088")));
        });
        var client = new JellyfinClient(new HttpClient(handler));

        var items = await client.GetLibraryAsync("http://jellyfin:8096", "key");

        Assert.Equal(["m1", "s1"], items.Select(i => i.JellyfinId).Order());
        Assert.All(handler.Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal)),
            r => Assert.Contains("ParentId=", r.RequestUri!.Query));
    }

    [Fact]
    public async Task GetLibrary_SkipsLibrariesWithoutId_AsksEachLibraryOnce_AndPagesEachOnItsOwn()
    {
        var total = JellyfinClient.PageSize + 1;
        var handler = new FakeHttpHandler(request =>
        {
            var query = request.RequestUri!.Query;
            if (request.RequestUri.AbsolutePath.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal))
                return Json("""[{"ItemId":"a"},{"ItemId":null},{"ItemId":""},{"ItemId":"A"},{"ItemId":"b"}]""");
            if (query.Contains("ParentId=b&"))
                return Json(ItemsJson(1, Series("s1", "Serie")));
            // Bibliothek a: Folgen zählen in Jellyfins Gesamtzahl mit, kommen hier aber nicht an. Die Seiten dürfen trotzdem nicht endlos weiterlaufen.
            return query.Contains("StartIndex=0&")
                ? Json(ItemsJson(total, [.. Enumerable.Range(0, JellyfinClient.PageSize).Select(i => Item($"e{i}", $"Folge {i}", "Episode"))]))
                : Json(ItemsJson(total, Movie("m1", "Film")));
        });
        var client = new JellyfinClient(new HttpClient(handler));

        var items = await client.GetLibraryAsync("http://jellyfin:8096", "key");

        Assert.Equal(["m1", "s1"], items.Select(i => i.JellyfinId).Order());
        var queries = handler.Requests.Skip(1).Select(r => r.RequestUri!.Query).ToList();
        Assert.Equal(3, queries.Count);
        Assert.Contains("ParentId=a&", queries[0]);
        Assert.Contains($"StartIndex={JellyfinClient.PageSize}&", queries[1]);
        Assert.Contains("ParentId=b&", queries[2]);
        Assert.Contains("StartIndex=0&", queries[2]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "abgelehnt")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task GetLibrary_FailingLibraryList_ThrowsReadableException_AndAsksNothingElse(HttpStatusCode status, string expectedText)
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal)
            ? new HttpResponseMessage(status)
            : Json(ItemsJson(1, Movie("m1", "Film"))));
        var client = new JellyfinClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<JellyfinException>(() => client.GetLibraryAsync("http://jellyfin:8096", "key"));

        Assert.Contains(expectedText, ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetLibrary_WithoutLibraries_IsEmpty()
    {
        var handler = new FakeHttpHandler(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/Library/VirtualFolders", StringComparison.Ordinal)
            ? VirtualFoldersJson()
            : ItemsJson(1, Movie("rest", "Übrig geblieben"))));
        var client = new JellyfinClient(new HttpClient(handler));

        Assert.Empty(await client.GetLibraryAsync("http://jellyfin:8096", "key"));
        Assert.Single(handler.Requests);
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

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
