using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
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
using WishListPage = Wunschkino.Components.Pages.Wishes.WishList;

namespace Wunschkino.Tests.Components;

public class WishListPageTests : ComponentTestBase
{
    private readonly TestDb _db;
    private readonly SettingsService _settings;
    private readonly WishService _wishes;
    private readonly User _alice;
    private readonly User _bob;
    private readonly User _admin;

    public WishListPageTests()
    {
        _db = Own(new TestDb());
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _alice = TestUsers.Add(_db, "alice");
        _bob = TestUsers.Add(_db, "bob");
        _admin = TestUsers.Add(_db, "admin", UserRole.Admin);

        var tmdb = new TmdbClient(new HttpClient(new FakeHttpHandler(TmdbResponse)));
        var aniList = new AniListClient(
            new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new MemoryCache(new MemoryCacheOptions()),
            new AniListThrottle(TimeProvider.System));
        _wishes = new WishService(_db, _settings, tmdb, aniList, new AnimeIdMapping(), new LibraryCache(), TimeProvider.System);

        Services.AddSingleton(_wishes);
        Services.AddSingleton(_settings);
        Services.AddSingleton(NullLogger<WishListPage>.Instance);
    }

    public override Task InitializeAsync() => _settings.SetAsync(SettingKeys.TmdbApiKey, "v3key", isProtected: true);

    private static HttpResponseMessage TmdbResponse(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/3/movie/603" => Json("""{"id":603,"title":"Matrix","release_date":"1999-03-30","poster_path":null,"imdb_id":"tt0133093"}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
    };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private Task WishAsync(User user) => _wishes.CreateOrJoinAsync(user.Id, 603, MediaKind.Movie, null);

    private IRenderedComponent<WishListPage> OpenAs(User user)
    {
        if (user.Role == UserRole.Admin)
            SignInAsAdmin(user.Id, user.Username);
        else
            SignInAsUser(user.Id, user.Username);
        var page = RenderPage<WishListPage>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".wish-card")), Patience);
        return page;
    }

    [Fact]
    public void WithoutWishes_SaysSo()
    {
        SignInAsUser(_alice.Id, "alice");

        var page = RenderPage<WishListPage>();

        page.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", page.Markup), Patience);
    }

    [Fact]
    public async Task ScopeInTheUrl_IsAppliedOnOpening_AndUnknownValuesFallBackToAll()
    {
        await WishAsync(_bob);
        SignInAsUser(_alice.Id, "alice");
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("/wishes?scope=mine");
        var mine = RenderPage<WishListPage>();
        mine.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", mine.Markup), Patience);

        navigation.NavigateTo("/wishes?scope=%27;--&status=99&sort=x");
        var fallback = RenderPage<WishListPage>();
        fallback.WaitForAssertion(() => Assert.NotEmpty(fallback.FindAll(".wish-card")), Patience);
    }

    [Fact]
    public async Task Wishes_ShowTheirStatusAsText()
    {
        await WishAsync(_bob);

        var page = OpenAs(_alice);

        Assert.Contains("Matrix", page.Markup);
        Assert.Contains("Offen", page.Markup);
    }

    [Fact]
    public async Task NamesAreHidden_ForUsers_AndTheNoteSaysSo()
    {
        await WishAsync(_bob);

        var page = OpenAs(_alice);

        Assert.Contains("Wünsche werden anonym angezeigt", page.Markup);
        Assert.DoesNotContain("von bob", page.Markup);
    }

    [Fact]
    public async Task Admins_SeeTheNames_AndNoAnonymityNote()
    {
        await WishAsync(_bob);

        var page = OpenAs(_admin);

        Assert.DoesNotContain("Wünsche werden anonym angezeigt", page.Markup);
        Assert.Contains("von bob", page.Markup);
    }

    [Fact]
    public async Task WhenNamesAreShown_NoAnonymityNoteAppears()
    {
        await _settings.SetAsync(SettingKeys.WishesAnonymous, "false", isProtected: false);
        await WishAsync(_bob);

        var page = OpenAs(_alice);

        Assert.DoesNotContain("Wünsche werden anonym angezeigt", page.Markup);
        Assert.Contains("von bob", page.Markup);
    }

    [Fact]
    public async Task AnotherUsersWish_CanBeSupportedWithPlusOne()
    {
        await WishAsync(_bob);
        var page = OpenAs(_alice);

        page.FindAll("button").Single(b => b.TextContent.Trim() == "+1").Click();

        Snackbars.WaitForAssertion(() => Assert.Contains("Du unterstützt jetzt", Snackbars.Markup), Patience);
        page.WaitForAssertion(() => Assert.DoesNotContain("+1", string.Join("", page.FindAll("button").Select(b => b.TextContent.Trim()))), Patience);
        Assert.Equal(2, (await _wishes.ListAsync(_alice.Id, WishScope.All, null, WishSort.Newest, viewerIsAdmin: false)).Single().Votes);
    }

    [Fact]
    public async Task OwnWish_CanBeWithdrawn()
    {
        await WishAsync(_alice);
        var page = OpenAs(_alice);

        page.FindAll("button").Single(b => b.TextContent.Contains("Zurückziehen")).Click();

        page.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", page.Markup), Patience);
    }

    [Fact]
    public async Task Admin_CanMarkAWishAsDone_AndTheStatusButtonsAreLabelled()
    {
        await WishAsync(_bob);
        var page = OpenAs(_admin);

        page.Find("button[aria-label^='Als erledigt markieren:']").Click();

        page.WaitForAssertion(() => Assert.Contains("Erledigt", page.Markup), Patience);
        Assert.Equal("true", page.Find("button[aria-label^='Als erledigt markieren:']").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Admin_MustConfirmDeleting_AndCancelingKeepsTheWish()
    {
        await WishAsync(_bob);
        var page = OpenAs(_admin);

        page.Find("button[aria-label^='Wunsch löschen:']").Click();
        Dialogs.WaitForAssertion(() => Assert.Contains("wirklich löschen", Dialogs.Markup), Patience);
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Abbrechen")).Click();

        Assert.Single((await _wishes.ListAsync(_admin.Id, WishScope.All, null, WishSort.Newest, viewerIsAdmin: true)));
    }

    [Fact]
    public async Task Admin_CanDeleteAfterConfirming()
    {
        await WishAsync(_bob);
        var page = OpenAs(_admin);

        page.Find("button[aria-label^='Wunsch löschen:']").Click();
        Dialogs.WaitForAssertion(() => Assert.Contains("wirklich löschen", Dialogs.Markup), Patience);
        Dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Löschen").Click();

        page.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", page.Markup), Patience);
    }

    private async Task<int> OnlyWishIdAsync() =>
        (await _wishes.ListAsync(_admin.Id, WishScope.All, null, WishSort.Newest, viewerIsAdmin: true)).Single().Id;

    [Fact]
    public async Task StatusInTheUrl_ShowsOnlyWishesWithThatStatus()
    {
        await WishAsync(_bob);
        await _wishes.SetStatusAsync(_admin.Id, await OnlyWishIdAsync(), WishStatus.Done, null);
        SignInAsUser(_alice.Id, "alice");
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("/wishes?status=done");
        var done = RenderPage<WishListPage>();
        done.WaitForAssertion(() => Assert.NotEmpty(done.FindAll(".wish-card")), Patience);

        navigation.NavigateTo("/wishes?status=REJECTED");
        var rejected = RenderPage<WishListPage>();
        rejected.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", rejected.Markup), Patience);
    }

    [Fact]
    public async Task ScopeMine_ListsOnlyWishesTheUserSupports()
    {
        await WishAsync(_bob);
        SignInAsUser(_alice.Id, "alice");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/wishes?scope=mine");
        var before = RenderPage<WishListPage>();
        before.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", before.Markup), Patience);

        await WishAsync(_alice);
        var after = RenderPage<WishListPage>();

        after.WaitForAssertion(() => Assert.NotEmpty(after.FindAll(".wish-card")), Patience);
    }

    [Fact]
    public async Task ChangingTheStatusFilter_ReloadsTheList_AndWritesItToTheUrl()
    {
        await WishAsync(_bob);
        var page = OpenAs(_alice);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var select = page.FindComponent<MudBlazor.MudSelect<WishStatus?>>();

        await page.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(WishStatus.Rejected));

        page.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", page.Markup), Patience);
        Assert.EndsWith("?status=rejected", navigation.Uri);
        await page.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(null));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".wish-card")), Patience);
        Assert.False(navigation.Uri.Contains("status="));
    }

    [Fact]
    public async Task ChangingScopeAndSort_WritesThemToTheUrl()
    {
        await WishAsync(_bob);
        var page = OpenAs(_alice);
        var navigation = Services.GetRequiredService<NavigationManager>();

        await page.InvokeAsync(() => page.FindComponent<MudBlazor.MudSelect<WishSort>>().Instance.ValueChanged.InvokeAsync(WishSort.MostVotes));
        page.WaitForAssertion(() => Assert.Contains("sort=mostvotes", navigation.Uri), Patience);
        await page.InvokeAsync(() => page.FindComponent<MudBlazor.MudSelect<WishScope>>().Instance.ValueChanged.InvokeAsync(WishScope.Mine));

        page.WaitForAssertion(() => Assert.Contains("Keine Wünsche gefunden.", page.Markup), Patience);
        Assert.Contains("scope=mine", navigation.Uri);
    }

    [Fact]
    public async Task Admin_CanAccept_Reject_AndReopenByPressingTheActiveButtonAgain()
    {
        await WishAsync(_bob);
        var page = OpenAs(_admin);

        page.Find("button[aria-label^='Als angenommen markieren:']").Click();
        page.WaitForAssertion(() => Assert.Equal("true", page.Find("button[aria-label^='Als angenommen markieren:']").GetAttribute("aria-pressed")), Patience);

        page.Find("button[aria-label^='Als abgelehnt markieren:']").Click();
        page.WaitForAssertion(() => Assert.Equal("true", page.Find("button[aria-label^='Als abgelehnt markieren:']").GetAttribute("aria-pressed")), Patience);
        Assert.Equal("false", page.Find("button[aria-label^='Als angenommen markieren:']").GetAttribute("aria-pressed"));

        page.Find("button[aria-label^='Als abgelehnt markieren:']").Click();
        page.WaitForAssertion(() => Assert.Equal("false", page.Find("button[aria-label^='Als abgelehnt markieren:']").GetAttribute("aria-pressed")), Patience);
        Assert.Equal(WishStatus.Open, (await _wishes.ListAsync(_admin.Id, WishScope.All, null, WishSort.Newest, viewerIsAdmin: true)).Single().Status);
    }

    [Fact]
    public async Task Admin_CanWriteAComment_UsersSeeIt_AndCancelingKeepsTheOldOne()
    {
        await WishAsync(_bob);
        var page = OpenAs(_admin);

        page.Find("button[aria-label^='Kommentar bearbeiten:']").Click();
        page.Find("textarea, input[type=text]").Change("Kommt nächste Woche");
        page.FindAll("button").Single(b => b.TextContent.Contains("Kommentar speichern")).Click();
        page.WaitForAssertion(() => Assert.Contains("Kommt nächste Woche", page.Markup), Patience);

        page.Find("button[aria-label^='Kommentar bearbeiten:']").Click();
        page.Find("textarea, input[type=text]").Change("anderer Text");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Abbrechen").Click();

        Assert.Contains("Kommt nächste Woche", page.Markup);
        Assert.DoesNotContain("anderer Text", page.Markup);
        Assert.Equal("Kommt nächste Woche", (await _wishes.ListAsync(_alice.Id, WishScope.All, null, WishSort.Newest, viewerIsAdmin: false)).Single().AdminComment);
    }

    [Fact]
    public async Task ASupporterWithOtherSupporters_CanOnlyTakeBackTheirOwnVote()
    {
        await WishAsync(_bob);
        await WishAsync(_alice);
        var page = OpenAs(_alice);

        page.FindAll("button").Single(b => b.TextContent.Contains("Meine Stimme zurückziehen")).Click();

        page.WaitForAssertion(() => Assert.DoesNotContain("Meine Stimme zurückziehen", page.Markup), Patience);
        Assert.Equal(1, (await _wishes.ListAsync(_bob.Id, WishScope.All, null, WishSort.Newest, viewerIsAdmin: false)).Single().Votes);
    }

    [Fact]
    public async Task RegularUsers_GetNoAdminButtons()
    {
        await WishAsync(_bob);

        var page = OpenAs(_alice);

        Assert.Empty(page.FindAll("button[aria-label^='Wunsch löschen:']"));
        Assert.Empty(page.FindAll("button[aria-label^='Als erledigt markieren:']"));
    }
}
