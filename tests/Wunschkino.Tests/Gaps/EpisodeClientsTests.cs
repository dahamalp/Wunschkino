using System.Net;
using System.Text;
using Wunschkino.Gaps;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Gaps;

public class JellyfinEpisodesTests
{
    private const string Url = "http://jellyfin.example:8096";
    private const string Key = "key123";

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetEpisodes_ReadsSeasonNumberAndRange_AndSkipsVirtualAndIncompleteItems()
    {
        var handler = new FakeHttpHandler(_ => Json("""
            {"Items":[
              {"ParentIndexNumber":1,"IndexNumber":1,"LocationType":"FileSystem"},
              {"ParentIndexNumber":1,"IndexNumber":2,"IndexNumberEnd":3,"LocationType":"FileSystem"},
              {"ParentIndexNumber":1,"IndexNumber":4,"LocationType":"Virtual"},
              {"ParentIndexNumber":2,"LocationType":"FileSystem"},
              {"IndexNumber":5,"LocationType":"FileSystem"}
            ]}
            """));
        var client = new JellyfinClient(new HttpClient(handler));

        var episodes = await client.GetEpisodesAsync(Url, Key, "series-1");

        Assert.Equal([new JellyfinEpisode(1, 1, null), new JellyfinEpisode(1, 2, 3)], episodes);
    }

    [Fact]
    public async Task GetEpisodes_AsksWithoutMissingPlaceholders_AndEscapesTheSeriesId()
    {
        var handler = new FakeHttpHandler(_ => Json("""{"Items":[]}"""));
        var client = new JellyfinClient(new HttpClient(handler));

        await client.GetEpisodesAsync(Url, Key, "ab/../c d");

        var uri = handler.Requests.Single().RequestUri!;
        Assert.Equal("/Shows/ab%2F..%2Fc%20d/Episodes", uri.AbsolutePath);
        Assert.Contains("IsMissing=false", uri.Query);
    }

    [Fact]
    public async Task GetEpisodes_WithoutItems_ReturnsAnEmptyList()
    {
        var client = new JellyfinClient(new HttpClient(new FakeHttpHandler(_ => Json("{}"))));

        Assert.Empty(await client.GetEpisodesAsync(Url, Key, "series-1"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "abgelehnt")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task GetEpisodes_ReportsProblemsAsJellyfinException(HttpStatusCode status, string fragment)
    {
        var client = new JellyfinClient(new HttpClient(new FakeHttpHandler(_ => Json("{}", status))));

        var exception = await Assert.ThrowsAsync<JellyfinException>(() => client.GetEpisodesAsync(Url, Key, "series-1"));

        Assert.Contains(fragment, exception.Message);
    }

    [Fact]
    public async Task GetEpisodes_RefusesAnApiKeyWithInvalidCharacters()
    {
        var client = new JellyfinClient(new HttpClient(new FakeHttpHandler(_ => Json("{}"))));

        await Assert.ThrowsAsync<JellyfinException>(() => client.GetEpisodesAsync(Url, "bad\r\nkey", "series-1"));
    }
}

public class TmdbEpisodesTests
{
    private const string Key = "v3key";

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TmdbClient Client(Func<string, HttpResponseMessage> respond, out List<string> paths)
    {
        var seen = new List<string>();
        paths = seen;
        return new TmdbClient(new HttpClient(new FakeHttpHandler(request =>
        {
            seen.Add(request.RequestUri!.AbsolutePath);
            return respond(request.RequestUri!.AbsolutePath);
        })));
    }

    [Fact]
    public async Task GetEpisodes_CollectsEverySeasonExceptSpecials_WithAirDates()
    {
        var client = Client(path => path switch
        {
            "/3/tv/1399" => Json("""{"seasons":[{"season_number":0},{"season_number":1},{"season_number":2}]}"""),
            "/3/tv/1399/season/1" => Json("""{"episodes":[{"episode_number":1,"air_date":"2011-04-17"},{"episode_number":2,"air_date":"2011-04-24"}]}"""),
            "/3/tv/1399/season/2" => Json("""{"episodes":[{"episode_number":1,"air_date":null},{"episode_number":2,"air_date":""}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }, out var paths);

        var episodes = await client.GetEpisodesAsync(Key, 1399);

        Assert.NotNull(episodes);
        Assert.Equal(
            [new TmdbEpisode(1, 1, new DateOnly(2011, 4, 17)), new TmdbEpisode(1, 2, new DateOnly(2011, 4, 24)), new TmdbEpisode(2, 1, null), new TmdbEpisode(2, 2, null)],
            episodes);
        Assert.DoesNotContain("/3/tv/1399/season/0", paths);
    }

    [Fact]
    public async Task GetEpisodes_ForAnUnknownSeries_ReturnsNull()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound), out _);

        Assert.Null(await client.GetEpisodesAsync(Key, 999));
    }

    [Fact]
    public async Task GetEpisodes_ASeasonWithoutData_IsSkipped_NotAnError()
    {
        var client = Client(path => path switch
        {
            "/3/tv/5" => Json("""{"seasons":[{"season_number":1},{"season_number":2}]}"""),
            "/3/tv/5/season/1" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "/3/tv/5/season/2" => Json("""{"episodes":[{"episode_number":1,"air_date":"2020-01-01"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }, out _);

        var episodes = await client.GetEpisodesAsync(Key, 5);

        Assert.Equal([new TmdbEpisode(2, 1, new DateOnly(2020, 1, 1))], episodes);
    }

    [Fact]
    public async Task GetEpisodes_TmdbProblems_ThrowTmdbException()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), out _);

        await Assert.ThrowsAsync<TmdbException>(() => client.GetEpisodesAsync(Key, 1399));
    }

    [Fact]
    public async Task GetEpisodes_IgnoresEpisodesWithoutAValidNumber_AndCapsTheSeasonCount()
    {
        var seasons = string.Join(',', Enumerable.Range(1, 150).Select(n => $"{{\"season_number\":{n}}}"));
        var client = Client(path => path == "/3/tv/7"
            ? Json("{\"seasons\":[" + seasons + "]}")
            : Json("""{"episodes":[{"episode_number":0,"air_date":"2020-01-01"},{"episode_number":1,"air_date":"2020-01-01"}]}"""),
            out var paths);

        var episodes = await client.GetEpisodesAsync(Key, 7);

        Assert.Equal(100, episodes!.Count);
        Assert.Equal(101, paths.Count);
    }
}
