using System.Net;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Services;

public class TmdbSearchTests
{
    private const string Key = "v3key";

    [Fact]
    public async Task Search_KeepsMoviesAndSeries_AndDropsPeople()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, """
            {"results":[
              {"id":603,"media_type":"movie","title":"Matrix","release_date":"1999-03-30","poster_path":"/matrix1.jpg"},
              {"id":1399,"media_type":"tv","name":"Game of Thrones","first_air_date":"2011-04-17","poster_path":null},
              {"id":6384,"media_type":"person","name":"Keanu Reeves"},
              {"id":5,"media_type":"movie","title":"  ","release_date":""}
            ]}
            """);
        var client = new TmdbClient(new HttpClient(handler));

        var results = await client.SearchAsync(Key, "matrix & more");

        Assert.Collection(results,
            movie =>
            {
                Assert.Equal(new TmdbTitle(603, MediaKind.Movie, "Matrix", 1999, "/matrix1.jpg", null), movie);
            },
            series =>
            {
                Assert.Equal(1399, series.TmdbId);
                Assert.Equal(MediaKind.Series, series.Kind);
                Assert.Equal("Game of Thrones", series.Title);
                Assert.Equal(2011, series.Year);
                Assert.Null(series.PosterPath);
            });
        var uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal("/3/search/multi", uri.AbsolutePath);
        Assert.Contains("query=matrix%20%26%20more", uri.Query);
        Assert.Contains("api_key=v3key", uri.Query);
    }

    [Fact]
    public async Task Search_DiscardsPosterPathsWithUnexpectedFormat()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"results":[{"id":1,"media_type":"movie","title":"Evil","poster_path":"/../../secret.jpg"}]}""");

        var results = await new TmdbClient(new HttpClient(handler)).SearchAsync(Key, "evil");

        Assert.Null(Assert.Single(results).PosterPath);
    }

    [Fact]
    public async Task FindByImdb_ReturnsMoviesAndSeries_WithTheImdbId()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, """
            {"movie_results":[{"id":603,"title":"Matrix","release_date":"1999-03-30","poster_path":"/matrix1.jpg"}],
             "tv_results":[{"id":1399,"name":"Serie","first_air_date":"2011-04-17"}]}
            """);

        var results = await new TmdbClient(new HttpClient(handler)).FindByImdbAsync(Key, "tt0133093");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("tt0133093", r.ImdbId));
        Assert.Equal(MediaKind.Movie, results[0].Kind);
        Assert.Equal(MediaKind.Series, results[1].Kind);
        var uri = Assert.Single(handler.Requests).RequestUri!;
        Assert.Equal("/3/find/tt0133093", uri.AbsolutePath);
        Assert.Contains("external_source=imdb_id", uri.Query);
    }

    [Fact]
    public async Task GetDetails_Movie_ReadsTopLevelImdbId()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"id":603,"title":"Matrix","release_date":"1999-03-30","poster_path":"/matrix1.jpg","imdb_id":"tt0133093"}""");

        var details = await new TmdbClient(new HttpClient(handler)).GetDetailsAsync(Key, 603, MediaKind.Movie);

        Assert.Equal(new TmdbTitle(603, MediaKind.Movie, "Matrix", 1999, "/matrix1.jpg", "tt0133093"), details);
        Assert.Equal("/3/movie/603", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetDetails_Series_ReadsImdbIdFromExternalIds()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"id":1399,"name":"Game of Thrones","first_air_date":"2011-04-17","external_ids":{"imdb_id":"tt0944947"}}""");

        var details = await new TmdbClient(new HttpClient(handler)).GetDetailsAsync(Key, 1399, MediaKind.Series);

        Assert.Equal("tt0944947", details!.ImdbId);
        Assert.Equal("/3/tv/1399", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("append_to_response=external_ids", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetDetails_UnknownTitle_ReturnsNull()
    {
        var client = new TmdbClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.NotFound)));

        Assert.Null(await client.GetDetailsAsync(Key, 999, MediaKind.Movie));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "abgelehnt")]
    [InlineData(HttpStatusCode.TooManyRequests, "keine weiteren Anfragen")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Search_ReportsTmdbProblemsWithReadableMessages(HttpStatusCode status, string expectedFragment)
    {
        var client = new TmdbClient(new HttpClient(FakeHttpHandler.Json(status)));

        var exception = await Assert.ThrowsAsync<TmdbException>(() => client.SearchAsync(Key, "x"));

        Assert.Contains(expectedFragment, exception.Message);
    }

    [Fact]
    public async Task SearchAndFind_On404_ThrowTmdbExceptionInsteadOfAnInternalType()
    {
        var client = new TmdbClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.NotFound)));

        await Assert.ThrowsAnyAsync<TmdbException>(() => client.SearchAsync(Key, "x"));
        await Assert.ThrowsAnyAsync<TmdbException>(() => client.FindByImdbAsync(Key, "tt0133093"));
    }

    [Fact]
    public async Task GetPoster_SvgResponse_IsRejected()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1]) { Headers = { ContentType = new("image/svg+xml") } }
        });

        Assert.Null(await new TmdbClient(new HttpClient(handler)).GetPosterAsync("/matrix1.jpg", TmdbPosterSize.Medium));
    }

    [Fact]
    public async Task Search_WhenTmdbIsUnreachableOrReturnsGarbage_ThrowsTmdbException()
    {
        var unreachable = new TmdbClient(new HttpClient(FakeHttpHandler.Throws(new HttpRequestException("down"))));
        var garbage = new TmdbClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, "<html>")));

        await Assert.ThrowsAsync<TmdbException>(() => unreachable.SearchAsync(Key, "x"));
        await Assert.ThrowsAsync<TmdbException>(() => garbage.SearchAsync(Key, "x"));
    }

    [Fact]
    public async Task Search_WithInvalidKeyCharacters_NeverSendsARequest()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK);

        await Assert.ThrowsAsync<TmdbException>(() => new TmdbClient(new HttpClient(handler)).SearchAsync("bad\r\nkey", "x"));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetPoster_RequestsTheFixedImageHost_WithTheRequestedSize()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/jpeg") } } });

        using var poster = await new TmdbClient(new HttpClient(handler)).GetPosterAsync("/matrix1.jpg", TmdbPosterSize.Large);

        Assert.NotNull(poster);
        Assert.Equal("https://image.tmdb.org/t/p/w500/matrix1.jpg", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Theory]
    [InlineData("/../etc/passwd")]
    [InlineData("//evil.example/x.jpg")]
    [InlineData("/a.jpg?x=1")]
    [InlineData("matrix1.jpg")]
    [InlineData("/matrix1.jpg\n")]
    public async Task GetPoster_RejectsInvalidPaths_WithoutARequest(string path)
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK);

        Assert.Null(await new TmdbClient(new HttpClient(handler)).GetPosterAsync(path, TmdbPosterSize.Medium));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetPoster_NonImageResponse_ReturnsNull()
    {
        var client = new TmdbClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, "{}")));

        Assert.Null(await client.GetPosterAsync("/matrix1.jpg", TmdbPosterSize.Medium));
    }

    [Theory]
    [InlineData(100, TmdbPosterSize.Small)]
    [InlineData(300, TmdbPosterSize.Medium)]
    [InlineData(600, TmdbPosterSize.Large)]
    [InlineData(null, TmdbPosterSize.Medium)]
    public void ParseSize_MapsWidthsToTheThreeSupportedSizes(int? width, TmdbPosterSize expected) =>
        Assert.Equal(expected, TmdbPosters.ParseSize(width));

    [Fact]
    public void ProxyUrl_IsNullForMissingOrInvalidPaths()
    {
        Assert.Equal("/tmdb-posters/abc12.jpg?w=185", TmdbPosters.ProxyUrl("/abc12.jpg", 185));
        Assert.Null(TmdbPosters.ProxyUrl(null));
        Assert.Null(TmdbPosters.ProxyUrl("/../x.jpg"));
    }
}
