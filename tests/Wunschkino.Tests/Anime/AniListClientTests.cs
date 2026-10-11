using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Wunschkino.Anime;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Anime;

public class AniListClientTests
{
    private const string TwoResults = """
        {"data":{"Page":{"media":[
          {"id":9989,"format":"TV","seasonYear":2011,"startDate":{"year":2011},"title":{"romaji":"Ano Hi Mita Hana no Namae wo Bokutachi wa Mada Shiranai.","english":"Anohana: The Flower We Saw That Day","native":"x"},"coverImage":{"large":"https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx9989-hImMg6kCMm6I.jpg"}},
          {"id":15039,"format":"MOVIE","startDate":{"year":2013},"title":{"romaji":"Anohana Movie","english":"  ","native":null},"coverImage":{"large":"https://evil.example/file/anilistcdn/media/anime/cover/medium/x.jpg"}},
          {"id":0,"title":{"romaji":"Kaputt"}},
          {"id":5,"title":{"romaji":" "}}
        ]}}}
        """;

    private static AniListClient CreateClient(HttpMessageHandler handler, AniListThrottle? throttle = null) =>
        new(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), throttle ?? new AniListThrottle(TimeProvider.System));

    [Fact]
    public async Task Search_MapsResults_AndDropsInvalidOnes()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, TwoResults);

        var results = await CreateClient(handler).SearchAsync("anohana");

        Assert.Collection(results,
            tv =>
            {
                Assert.Equal(9989, tv.Id);
                Assert.Equal("Anohana: The Flower We Saw That Day", tv.DisplayTitle);
                Assert.Equal("TV", tv.Format);
                Assert.Equal(2011, tv.Year);
                Assert.Equal("/file/anilistcdn/media/anime/cover/medium/bx9989-hImMg6kCMm6I.jpg", tv.CoverPath);
            },
            movie =>
            {
                Assert.Equal("Anohana Movie", movie.DisplayTitle);
                Assert.Null(movie.English);
                Assert.Equal(2013, movie.Year);
                Assert.Null(movie.CoverPath);
            });
    }

    [Fact]
    public async Task Search_PostsAnAnonymousGraphQlQuery_WithoutAdultContent()
    {
        string? sentBody = null;
        var handler = new FakeHttpHandler(request =>
        {
            // Der Client entsorgt den Request nach dem Senden, deshalb wird der Body schon hier gelesen.
            sentBody = request.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoResults, Encoding.UTF8, "application/json") };
        });

        await CreateClient(handler).SearchAsync("  Ano Hana ");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://graphql.anilist.co/", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        using var body = JsonDocument.Parse(sentBody!);
        Assert.Contains("isAdult: false", body.RootElement.GetProperty("query").GetString());
        Assert.Equal("Ano Hana", body.RootElement.GetProperty("variables").GetProperty("q").GetString());
    }

    [Fact]
    public async Task Search_CachesIdenticalQueries_IgnoringCaseAndWhitespace()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, TwoResults);
        var client = CreateClient(handler);

        await client.SearchAsync("Anohana");
        await client.SearchAsync("  anohana ");

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Search_RateLimitedByAniList_ReportsRetryAfter()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45));
            return response;
        });

        var exception = await Assert.ThrowsAsync<AniListException>(() => CreateClient(handler).SearchAsync("x"));

        Assert.Contains("45 Sekunden", exception.Message);
    }

    [Fact]
    public async Task Search_IgnoresNullElementsInTheMediaList()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, """
            {"data":{"Page":{"media":[null,{"id":1,"format":"TV","title":{"romaji":"Eins","english":null,"native":null},"coverImage":null}]}}}
            """);

        var results = await CreateClient(handler).SearchAsync("eins");

        Assert.Equal(1, Assert.Single(results).Id);
    }

    [Fact]
    public async Task Search_After429_DoesNotAskAniListAgainWhileTheBlockLasts()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45));
            return response;
        });
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<AniListException>(() => client.SearchAsync("erste"));

        var exception = await Assert.ThrowsAsync<AniListException>(() => client.SearchAsync("zweite"));

        Assert.Contains("ausgelastet", exception.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Search_RespectsTheSharedRequestBudget_WithoutCallingAniListAgain()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, TwoResults);
        var client = CreateClient(handler);
        for (var i = 0; i < AniListThrottle.MaxRequestsPerMinute; i++)
            await client.SearchAsync($"suche {i}", userId: i / AniListThrottle.MaxRequestsPerUserPerMinute);

        var exception = await Assert.ThrowsAsync<AniListException>(() => client.SearchAsync("eine zu viel", userId: 99));

        Assert.Contains("ausgelastet", exception.Message);
        Assert.Equal(AniListThrottle.MaxRequestsPerMinute, handler.Requests.Count);
    }

    [Fact]
    public async Task Search_OneUserOverTheirOwnBudget_DoesNotBlockOtherUsers()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, TwoResults);
        var client = CreateClient(handler);
        for (var i = 0; i < AniListThrottle.MaxRequestsPerUserPerMinute; i++)
            await client.SearchAsync($"suche {i}", userId: 1);

        var exception = await Assert.ThrowsAsync<AniListException>(() => client.SearchAsync("eine zu viel", userId: 1));
        var other = await client.SearchAsync("andere suche", userId: 2);

        Assert.Contains("Du suchst gerade sehr oft", exception.Message);
        Assert.NotEmpty(other);
        Assert.Equal(AniListThrottle.MaxRequestsPerUserPerMinute + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task Search_RepeatedQueriesFromTheCache_DoNotCountAgainstTheBudget()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, TwoResults);
        var client = CreateClient(handler);

        for (var i = 0; i < AniListThrottle.MaxRequestsPerUserPerMinute * 3; i++)
            await client.SearchAsync("anohana", userId: 1);

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "HTTP 500")]
    [InlineData(HttpStatusCode.OK, """{"errors":[{"message":"boom"}]}""", "nicht verarbeiten")]
    [InlineData(HttpStatusCode.OK, "<html>", "unerwartete Antwort")]
    [InlineData(HttpStatusCode.OK, "null", "leere Antwort")]
    public async Task Search_ReportsProblemsWithReadableMessages(HttpStatusCode status, string body, string expectedFragment)
    {
        var client = CreateClient(FakeHttpHandler.Json(status, body));

        var exception = await Assert.ThrowsAsync<AniListException>(() => client.SearchAsync("x"));

        Assert.Contains(expectedFragment, exception.Message);
    }

    [Fact]
    public async Task Search_Unreachable_ThrowsReadableException()
    {
        var client = CreateClient(FakeHttpHandler.Throws(new HttpRequestException("down")));

        var exception = await Assert.ThrowsAsync<AniListException>(() => client.SearchAsync("x"));

        Assert.Contains("nicht erreichbar", exception.Message);
    }

    [Fact]
    public async Task GetCover_RequestsTheFixedCdnHost_AndAcceptsOnlyRasterImages()
    {
        const string path = "/file/anilistcdn/media/anime/cover/medium/bx9989-hImMg6kCMm6I.jpg";
        var jpeg = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) { Headers = { ContentType = new("image/jpeg") } } });
        var svg = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) { Headers = { ContentType = new("image/svg+xml") } } });

        using var cover = await CreateClient(jpeg).GetCoverAsync(path);

        Assert.NotNull(cover);
        Assert.Equal("https://s4.anilist.co" + path, Assert.Single(jpeg.Requests).RequestUri!.ToString());
        Assert.Null(await CreateClient(svg).GetCoverAsync(path));
    }

    [Theory]
    [InlineData("/file/anilistcdn/media/anime/cover/medium/../../../secret.jpg")]
    [InlineData("//evil.example/x.jpg")]
    [InlineData("/file/anilistcdn/media/anime/cover/medium/bx1.jpg?x=1")]
    [InlineData("/file/anilistcdn/media/anime/cover/medium/bx1.jpg\n")]
    [InlineData("/other/path.jpg")]
    public async Task GetCover_RejectsInvalidPaths_WithoutARequest(string path)
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK);

        Assert.Null(await CreateClient(handler).GetCoverAsync(path));
        Assert.Empty(handler.Requests);
    }
}

public class AniListCoversTests
{
    [Theory]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx9989-hImMg6kCMm6I.jpg", true)]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/cover/large/b1-x.png", true)]
    [InlineData("http://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx1.jpg", false)]
    [InlineData("https://evil.example/file/anilistcdn/media/anime/cover/medium/bx1.jpg", false)]
    [InlineData("https://s4.anilist.co.evil.example/file/anilistcdn/media/anime/cover/medium/bx1.jpg", false)]
    [InlineData("https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx1.svg", false)]
    [InlineData(null, false)]
    [InlineData("kein url", false)]
    public void TryGetPath_AcceptsOnlyTheKnownCdnFormat(string? url, bool accepted) =>
        Assert.Equal(accepted, AniListCovers.TryGetPath(url) is not null);

    [Fact]
    public void ProxyUrl_IsNullForMissingOrInvalidPaths()
    {
        Assert.Equal("/anilist-covers/file/anilistcdn/media/anime/cover/medium/bx1.jpg",
            AniListCovers.ProxyUrl("/file/anilistcdn/media/anime/cover/medium/bx1.jpg"));
        Assert.Null(AniListCovers.ProxyUrl(null));
        Assert.Null(AniListCovers.ProxyUrl("/../x.jpg"));
    }
}

internal static class AniListClientTestExtensions
{
    private const int DefaultUserId = 1;

    /// <summary>Die meisten Tests brauchen nur irgendeinen Nutzer; Tests zum Limit geben die Nutzerkennung selbst an.</summary>
    public static Task<IReadOnlyList<AniListAnime>> SearchAsync(this AniListClient client, string query) =>
        client.SearchAsync(query, DefaultUserId);
}

public class AniListThrottleTests
{
    [Fact]
    public void TryAcquire_BlocksAfterTheSharedBudget_AndReleasesAfterAMinute()
    {
        var time = new ManualTimeProvider();
        var throttle = new AniListThrottle(time);
        for (var i = 0; i < AniListThrottle.MaxRequestsPerMinute; i++)
            Assert.Null(throttle.TryAcquire(i / AniListThrottle.MaxRequestsPerUserPerMinute));

        var wait = throttle.TryAcquire(99);

        Assert.NotNull(wait);
        Assert.False(wait.Value.BecauseOfThisUser);
        Assert.InRange(wait.Value.Duration, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(throttle.TryAcquire(99));
    }

    [Fact]
    public void TryAcquire_LimitsEachUserOnTheirOwn_AndReleasesThemAfterAMinute()
    {
        var time = new ManualTimeProvider();
        var throttle = new AniListThrottle(time);
        for (var i = 0; i < AniListThrottle.MaxRequestsPerUserPerMinute; i++)
            Assert.Null(throttle.TryAcquire(1));

        var wait = throttle.TryAcquire(1);

        Assert.NotNull(wait);
        Assert.True(wait.Value.BecauseOfThisUser);
        Assert.Null(throttle.TryAcquire(2));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(throttle.TryAcquire(1));
    }

    [Fact]
    public void TryAcquire_ForgetsIdleUsers_SoMemoryDoesNotGrowWithEveryUserEver()
    {
        var time = new ManualTimeProvider();
        var throttle = new AniListThrottle(time);
        for (var user = 0; user < 150; user++)
        {
            Assert.Null(throttle.TryAcquire(user));
            time.Advance(TimeSpan.FromSeconds(3));
        }

        // Ab 100 bekannten Nutzern werden abgelaufene Zähler beim Eintreffen eines neuen Nutzers entfernt.
        Assert.InRange(throttle.TrackedUsers, 1, 99);
    }

    [Fact]
    public void TryAcquire_RejectedRequests_AreNotCounted()
    {
        var time = new ManualTimeProvider();
        var throttle = new AniListThrottle(time);
        for (var i = 0; i < AniListThrottle.MaxRequestsPerUserPerMinute; i++)
            Assert.Null(throttle.TryAcquire(1));
        for (var i = 0; i < 20; i++)
            Assert.NotNull(throttle.TryAcquire(1));

        // Die Ablehnungen haben nichts verbraucht: Nach einer Minute steht das volle Kontingent wieder bereit.
        time.Advance(TimeSpan.FromSeconds(61));
        for (var i = 0; i < AniListThrottle.MaxRequestsPerUserPerMinute; i++)
            Assert.Null(throttle.TryAcquire(1));
    }

    [Fact]
    public void BlockFor_HoldsBackRequestsUntilTheBlockEnds_AndNeverShortensAnExistingBlock()
    {
        var time = new ManualTimeProvider();
        var throttle = new AniListThrottle(time);

        throttle.BlockFor(TimeSpan.FromSeconds(60));
        throttle.BlockFor(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(60), throttle.TryAcquire(1)!.Value.Duration);
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(throttle.TryAcquire(1));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
