using System.Net;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Anime;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;
using HomePage = Wunschkino.Components.Pages.Home;

namespace Wunschkino.Tests.Components;

public class HomePageTests : ComponentTestBase
{
    private readonly LibraryCache _cache = new();

    public HomePageTests()
    {
        var db = Own(new TestDb());
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        Services.AddSingleton(_cache);
        Services.AddSingleton(new LibraryRefreshService(
            scopes,
            _cache,
            new WishAutoCompleter(db, new AnimeIdMapping(), TimeProvider.System, NullLogger<WishAutoCompleter>.Instance),
            TimeProvider.System,
            NullLogger<LibraryRefreshService>.Instance));
    }

    private static LibraryItem Movie(string id, string title, int? year = null) => JellyfinFixtures.LibraryItem(id, title, MediaKind.Movie, year);

    private static LibraryItem Series(string id, string title, int? year = null) => JellyfinFixtures.LibraryItem(id, title, MediaKind.Series, year);

    private void Fill(params LibraryItem[] items) => _cache.Update(items, DateTime.UtcNow);

    private static IReadOnlyList<string> Titles(IRenderedComponent<HomePage> page) =>
        page.FindAll(".library-title").Select(e => e.TextContent.Trim()).ToList();

    private static IElement? RefreshButton(IRenderedComponent<HomePage> page) =>
        page.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Jetzt aktualisieren") || b.TextContent.Contains("Wird aktualisiert"));

    [Fact]
    public void ShowsCountsPerKind_AndSortsTitlesNaturally()
    {
        SignInAsUser(2, "alice");
        Fill(Series("a", "Staffel 10"), Series("b", "Staffel 2"), Movie("c", "Arcane Film"), Series("d", "Staffel 1"));

        var page = RenderPage<HomePage>();

        Assert.Contains("1 Filme · 3 Serien", page.Markup);
        Assert.Equal(["Arcane Film", "Staffel 1", "Staffel 2", "Staffel 10"], Titles(page));
    }

    [Fact]
    public void Search_NarrowsTheGrid_AndSaysSoWhenNothingMatches()
    {
        SignInAsUser(2, "alice");
        Fill(Movie("a", "Matrix", 1999), Movie("b", "Arcane", 2021));
        var page = RenderPage<HomePage>();
        // Das Feld wird nach jedem Rendern neu gesucht: Ein gemerktes Element gehört sonst zu einem veralteten Render-Stand.
        IElement SearchBox() => page.FindAll("input").First(i => i.GetAttribute("type") == "text");

        SearchBox().Input("matri");
        page.WaitForAssertion(() => Assert.Equal(["Matrix"], Titles(page)), Patience);

        SearchBox().Input("gibt es nicht");
        page.WaitForAssertion(() => Assert.Contains("Keine Treffer.", page.Markup), Patience);
        Assert.Empty(Titles(page));
    }

    [Fact]
    public void FiltersInTheUrl_AreAppliedOnOpening()
    {
        SignInAsUser(2, "alice");
        Fill(Movie("a", "Matrix", 1999), Series("b", "Arcane", 2021), Series("c", "Babylon 5", 1994), Series("d", "Zeta", 2023));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/?type=series&sort=yeardescending&q=a");

        var page = RenderPage<HomePage>();

        Assert.Equal(["Zeta", "Arcane", "Babylon 5"], Titles(page));
    }

    [Fact]
    public void UnknownFiltersInTheUrl_FallBackToTheDefaults()
    {
        SignInAsUser(2, "alice");
        Fill(Series("a", "Zeta"), Movie("b", "Arcane"));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/?type=7&sort=drop%20table");

        var page = RenderPage<HomePage>();

        Assert.Equal(["Arcane", "Zeta"], Titles(page));
    }

    [Fact]
    public void Search_IsWrittenToTheUrl_WithoutAddingAHistoryEntry()
    {
        SignInAsUser(2, "alice");
        Fill(Movie("a", "Matrix"));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var page = RenderPage<HomePage>();

        page.FindAll("input").First(i => i.GetAttribute("type") == "text").Input("matri");

        page.WaitForAssertion(() => Assert.EndsWith("?q=matri", navigation.Uri), Patience);
        Assert.True(((BunitNavigationManager)navigation).History.First().Options.ReplaceHistoryEntry);
    }

    [Fact]
    public async Task ChoosingATypeAndASort_FiltersTheGrid_AndWritesBothToTheUrl()
    {
        SignInAsUser(2, "alice");
        Fill(Movie("a", "Matrix", 1999), Series("b", "Arcane", 2021), Series("c", "Babylon 5", 1994));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var page = RenderPage<HomePage>();

        await page.InvokeAsync(() => page.FindComponent<MudBlazor.MudSelect<MediaKind?>>().Instance.ValueChanged.InvokeAsync(MediaKind.Series));
        page.WaitForAssertion(() => Assert.Equal(["Arcane", "Babylon 5"], Titles(page)), Patience);
        Assert.Contains("type=series", navigation.Uri);

        await page.InvokeAsync(() => page.FindComponent<MudBlazor.MudSelect<LibrarySort>>().Instance.ValueChanged.InvokeAsync(LibrarySort.YearDescending));
        page.WaitForAssertion(() => Assert.Equal(["Arcane", "Babylon 5"], Titles(page)), Patience);
        Assert.Contains("sort=yeardescending", navigation.Uri);

        await page.InvokeAsync(() => page.FindComponent<MudBlazor.MudSelect<MediaKind?>>().Instance.ValueChanged.InvokeAsync(null));
        page.WaitForAssertion(() => Assert.Equal(["Arcane", "Matrix", "Babylon 5"], Titles(page)), Patience);
        Assert.DoesNotContain("type=", navigation.Uri);
    }

    [Fact]
    public async Task ChangingTheTypeFilter_GoesBackToTheFirstPage()
    {
        SignInAsUser(2, "alice");
        Fill(Enumerable.Range(1, 70).Select(i => Movie($"m{i}", $"Film {i:000}")).Append(Series("s", "Serie")).ToArray());
        var page = RenderPage<HomePage>();
        page.FindAll("button").Single(b => b.TextContent.Contains("Mehr anzeigen")).Click();
        page.WaitForAssertion(() => Assert.Equal(71, Titles(page).Count), Patience);

        await page.InvokeAsync(() => page.FindComponent<MudBlazor.MudSelect<MediaKind?>>().Instance.ValueChanged.InvokeAsync(MediaKind.Movie));

        page.WaitForAssertion(() => Assert.Equal(60, Titles(page).Count), Patience);
    }

    [Fact]
    public void Paging_ShowsSixtyAtFirst_AndTheRestAfterMore()
    {
        SignInAsUser(2, "alice");
        Fill(Enumerable.Range(1, 70).Select(i => Movie($"m{i}", $"Film {i:000}")).ToArray());
        var page = RenderPage<HomePage>();

        Assert.Equal(60, Titles(page).Count);
        Assert.Contains("Mehr anzeigen (10 weitere)", page.Markup);
        page.FindAll("button").Single(b => b.TextContent.Contains("Mehr anzeigen")).Click();

        page.WaitForAssertion(() => Assert.Equal(70, Titles(page).Count), Patience);
        Assert.DoesNotContain("Mehr anzeigen", page.Markup);
    }

    [Fact]
    public async Task RefreshButton_IsOnlyForAdmins()
    {
        Fill(Movie("a", "Matrix"));
        SignInAsUser(2, "alice");

        var asUser = RenderPage<HomePage>();

        // Die Rollenprüfung löst sich asynchron auf (beim Admin braucht derselbe Button ein WaitForAssertion).
        // Eine Abwesenheit beweist deshalb erst etwas, wenn man der Seite diese Zeit gegeben hat.
        await Task.Delay(300);
        Assert.Equal(["Matrix"], Titles(asUser));
        Assert.Null(RefreshButton(asUser));
    }

    [Fact]
    public void RefreshButton_ForAdmins_WaitsUntilTheLibraryHasBeenReloaded()
    {
        SignInAsAdmin();
        Fill(Movie("a", "Matrix"));
        var page = RenderPage<HomePage>();
        page.WaitForAssertion(() => Assert.NotNull(RefreshButton(page)), Patience);

        RefreshButton(page)!.Click();
        page.WaitForAssertion(() => Assert.True(RefreshButton(page)!.HasAttribute("disabled")), Patience);
        Assert.Contains("Wird aktualisiert", page.Markup);

        Fill(Movie("a", "Matrix"), Movie("b", "Arcane"));

        page.WaitForAssertion(() =>
        {
            Assert.False(RefreshButton(page)!.HasAttribute("disabled"));
            Assert.Equal(["Arcane", "Matrix"], Titles(page));
        }, Patience);
    }

    [Fact]
    public void LoadError_WithoutItems_IsShownAsAWarning()
    {
        SignInAsUser(2, "alice");
        _cache.ReportError("Jellyfin ist noch nicht eingerichtet.");

        var page = RenderPage<HomePage>();

        Assert.Contains("Jellyfin ist noch nicht eingerichtet.", page.Markup);
        Assert.DoesNotContain("letzte erfolgreich geladene Stand", page.Markup);
    }

    [Fact]
    public void LoadError_WithItems_KeepsShowingThemAndSaysTheyAreOld()
    {
        SignInAsUser(2, "alice");
        Fill(Movie("a", "Matrix"));
        _cache.ReportError("Jellyfin antwortet nicht.");

        var page = RenderPage<HomePage>();

        Assert.Contains("Jellyfin antwortet nicht.", page.Markup);
        Assert.Contains("letzte erfolgreich geladene Stand", page.Markup);
        Assert.Equal(["Matrix"], Titles(page));
    }
}
