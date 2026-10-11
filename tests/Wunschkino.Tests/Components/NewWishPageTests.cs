using System.Net;
using System.Text;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;
using NewWishPage = Wunschkino.Components.Pages.Wishes.NewWish;

namespace Wunschkino.Tests.Components;

public class NewWishPageTests : ComponentTestBase
{
    private const string AniListJson = """
        {"data":{"Page":{"media":[
          {"id":9989,"format":"TV","seasonYear":2011,"startDate":{"year":2011},"title":{"romaji":"Ano Hi Mita Hana","english":"Anohana","native":"x"},"coverImage":{"large":null}},
          {"id":777,"format":"OVA","startDate":{"year":2010},"title":{"romaji":"Ohne Zuordnung","english":null,"native":null},"coverImage":{"large":null}},
          {"id":888,"format":"TV","startDate":{"year":2015},"title":{"romaji":"Schon da","english":null,"native":null},"coverImage":{"large":null}}
        ]}}}
        """;

    private const string AniListOnlyUnmappedJson = """
        {"data":{"Page":{"media":[
          {"id":777,"format":"OVA","startDate":{"year":2010},"title":{"romaji":"Ohne Zuordnung","english":null,"native":null},"coverImage":{"large":null}}
        ]}}}
        """;

    private readonly TestDb _db;
    private readonly SettingsService _settings;
    private readonly LibraryCache _library = new();
    private readonly AnimeIdMapping _mapping = new();
    private readonly WishService _wishes;
    private readonly User _alice;
    private readonly User _bob;
    private string _aniListJson = AniListJson;

    public NewWishPageTests()
    {
        _db = Own(new TestDb());
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _alice = TestUsers.Add(_db, "alice");
        _bob = TestUsers.Add(_db, "bob");
        SignInAsUser(_alice.Id, "alice");

        _mapping.Replace(new Dictionary<int, AnimeMapping>
        {
            [9989] = new(9989, MediaKind.Series, 1399, []),
            [888] = new(888, null, null, ["tt0000888"])
        }, DateTime.UtcNow);
        _library.Update([JellyfinFixtures.LibraryItem("j1", "Schon da", MediaKind.Series, imdb: "tt0000888")], DateTime.UtcNow);

        var tmdb = new TmdbClient(new HttpClient(new FakeHttpHandler(TmdbResponse)));
        var aniList = new AniListClient(
            new HttpClient(new FakeHttpHandler(_ => Json(_aniListJson))),
            new MemoryCache(new MemoryCacheOptions()),
            new AniListThrottle(TimeProvider.System));
        _wishes = new WishService(_db, _settings, tmdb, aniList, _mapping, _library, TimeProvider.System);

        Services.AddSingleton(_wishes);
        Services.AddSingleton(NullLogger<NewWishPage>.Instance);
    }

    public override Task InitializeAsync() => _settings.SetAsync(SettingKeys.TmdbApiKey, "v3key", isProtected: true);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TmdbResponse(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/3/search/multi" => Json("""{"results":[{"id":603,"media_type":"movie","title":"Matrix","release_date":"1999-03-30","poster_path":"/matrix1.jpg"}]}"""),
        "/3/movie/603" => Json("""{"id":603,"title":"Matrix","release_date":"1999-03-30","poster_path":"/matrix1.jpg","imdb_id":"tt0133093"}"""),
        "/3/tv/1399" => Json("""{"id":1399,"name":"Game of Thrones","first_air_date":"2011-04-17","poster_path":"/thrones1.jpg","external_ids":{"imdb_id":"tt0944947"}}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
    };

    private IRenderedComponent<NewWishPage> OpenPage()
    {
        var page = RenderPage<NewWishPage>();
        page.WaitForAssertion(() => Assert.Contains("Wunsch äußern", page.Markup), Patience);
        return page;
    }

    private static void Search(IRenderedComponent<NewWishPage> page, string query)
    {
        page.FindAll("input").First(i => i.GetAttribute("type") == "text").Input(query);
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Suchen").Click();
    }

    private static void SwitchToAnime(IRenderedComponent<NewWishPage> page) =>
        page.FindAll(".mud-toggle-item").Single(item => item.TextContent.Contains("Anime")).Click();

    private static IElement Button(IRenderedComponent<NewWishPage> page, string text) =>
        page.FindAll("button").First(b => b.TextContent.Trim() == text);

    [Fact]
    public void AnimeSearch_ShowsWishableTitles_HidesUnmappedOnes_ButKeepsThoseAlreadyInTheLibrary()
    {
        var page = OpenPage();
        SwitchToAnime(page);

        Search(page, "anohana");

        page.WaitForAssertion(() => Assert.Contains("Anohana", page.Markup), Patience);
        Assert.Contains("Schon da", page.Markup);
        Assert.Contains("Schon in Jellyfin", page.Markup);
        // Genau ein Titel ist wünschbar: Anohana. „Schon da“ liegt in Jellyfin, „Ohne Zuordnung“ ist ausgeblendet.
        Assert.Single(page.FindAll("button"), b => b.TextContent.Trim() == "Wünschen");
        Assert.DoesNotContain("Ohne Zuordnung", page.Markup);
    }

    [Fact]
    public void AnimeSearch_WithOnlyUnmappedHits_SaysNothingWasFound()
    {
        _aniListJson = AniListOnlyUnmappedJson;
        var page = OpenPage();
        SwitchToAnime(page);

        Search(page, "ohne zuordnung");

        page.WaitForAssertion(() => Assert.Contains("Nichts gefunden.", page.Markup), Patience);
        Assert.DoesNotContain("Ohne Zuordnung", page.Markup);
    }

    [Fact]
    public async Task AnimeSearch_ForAnAlreadyWishedTitle_ShowsVotes_AndJoiningAddsMine()
    {
        await _wishes.CreateOrJoinAsync(_bob.Id, 1399, MediaKind.Series, null);
        var page = OpenPage();
        SwitchToAnime(page);

        Search(page, "anohana");

        page.WaitForAssertion(() => Assert.Contains("Schon gewünscht", page.Markup), Patience);
        Assert.Contains("1 Stimme", page.Markup);
        Button(page, "+1").Click();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Du unterstützt diesen Wunsch.", page.Markup);
            Assert.Contains("2 Stimmen", page.Markup);
        }, Patience);
    }

    [Fact]
    public void MediaSearch_WishingATitle_ConfirmsInASnackbar_AndMarksItAsWished()
    {
        var page = OpenPage();

        Search(page, "matrix");
        page.WaitForAssertion(() => Assert.Contains("Matrix", page.Markup), Patience);
        Button(page, "Wünschen").Click();

        Snackbars.WaitForAssertion(() => Assert.Contains("steht jetzt auf der Wunschliste", Snackbars.Markup), Patience);
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Schon gewünscht", page.Markup);
            Assert.Contains("Du unterstützt diesen Wunsch.", page.Markup);
        }, Patience);
    }

    [Fact]
    public void MediaSearch_WithoutTmdbKey_ShowsTheProblemAsAWarning_NotAnEmptyResult()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Settings.RemoveRange(db.Settings);
            db.SaveChanges();
        }
        var page = OpenPage();

        Search(page, "matrix");

        page.WaitForAssertion(() => Assert.Contains("TMDB", page.Markup), Patience);
        Assert.DoesNotContain("Nichts gefunden.", page.Markup);
    }
}
