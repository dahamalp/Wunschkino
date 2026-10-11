using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Wishes;
using Wunschkino.Gaps;
using Wunschkino.Library;
using Wunschkino.Tests.Helpers;
using GapsPage = Wunschkino.Components.Pages.Admin.Gaps;

namespace Wunschkino.Tests.Components;

public class GapsPageTests : ComponentTestBase
{
    private readonly TestDb _db;
    private readonly LibraryCache _library = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly SeriesGapProgress _progress;

    public GapsPageTests()
    {
        _db = Own(new TestDb());
        _progress = new SeriesGapProgress(_time);
        SignInAsAdmin();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(_db);
        Services.AddSingleton(_library);
        Services.AddSingleton(_progress);
        Services.AddSingleton(new LibraryRefreshService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _library,
            new WishAutoCompleter(_db, new AnimeIdMapping(), TimeProvider.System, NullLogger<WishAutoCompleter>.Instance),
            TimeProvider.System,
            NullLogger<LibraryRefreshService>.Instance));
    }

    private static AngleSharp.Dom.IElement? ScanButton(IRenderedComponent<GapsPage> page) =>
        page.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Alle jetzt prüfen") || b.TextContent.Contains("Wird geprüft"));

    private void Add(string title, GapStatus status, int expected, int present, bool hasResult = true, params SeasonGap[] seasons)
    {
        using var db = _db.CreateDbContext();
        db.SeriesGaps.Add(new SeriesGap
        {
            JellyfinId = Guid.NewGuid().ToString("N"),
            TmdbId = 1,
            Title = title,
            HasResult = hasResult,
            Status = status,
            ExpectedCount = expected,
            PresentCount = present,
            MissingJson = SeriesGapSerializer.Serialize(seasons),
            LastAttemptUtc = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc)
        });
        db.SaveChanges();
    }

    private void SeriesInLibrary(int count) =>
        _library.Update(Enumerable.Range(1, count)
            .Select(i => JellyfinFixtures.LibraryItem($"s{i}", $"Serie {i}", MediaKind.Series, tmdb: i.ToString()))
            .ToList(), DateTime.UtcNow);

    private IRenderedComponent<GapsPage> OpenPage()
    {
        var page = RenderPage<GapsPage>();
        page.WaitForAssertion(() => Assert.Contains("Serien geprüft", page.Markup), TimeSpan.FromSeconds(5));
        return page;
    }

    [Fact]
    public void WithoutAnyResult_ExplainsThatNothingWasCheckedYet()
    {
        SeriesInLibrary(3);

        var page = OpenPage();

        Assert.Contains("0 von 3 Serien geprüft", page.Markup);
        Assert.Contains("noch keine Serie geprüft", page.Markup);
        Assert.DoesNotContain("Keine fehlenden Folgen gefunden", page.Markup);
    }

    [Fact]
    public void WhenEverythingIsComplete_SaysSo()
    {
        SeriesInLibrary(1);
        Add("Vollständig", GapStatus.Complete, 10, 10);

        var page = OpenPage();

        Assert.Contains("Keine fehlenden Folgen gefunden.", page.Markup);
        Assert.DoesNotContain("noch keine Serie geprüft", page.Markup);
    }

    [Fact]
    public void ListsMissingEpisodesReadably_AndKeepsUncertainSeriesApart()
    {
        SeriesInLibrary(2);
        Add("Lückenserie", GapStatus.Missing, 14, 11, true, new SeasonGap(1, [3, 5, 6], false), new SeasonGap(2, [1, 2], true));
        Add("Anime", GapStatus.UncertainNumbering, 12, 10, true, new SeasonGap(1, [11, 12], false));

        var page = OpenPage();

        var markup = page.Markup;
        var missingHeading = markup.IndexOf("Fehlende Folgen (1)", StringComparison.Ordinal);
        var uncertainHeading = markup.IndexOf("Zählung unsicher (1)", StringComparison.Ordinal);
        Assert.True(missingHeading >= 0 && uncertainHeading > missingHeading, "Beide Abschnitte müssen vorhanden und in dieser Reihenfolge stehen.");

        // Jede Serie muss im richtigen Abschnitt stehen, nicht nur irgendwo auf der Seite.
        var certainPart = markup[missingHeading..uncertainHeading];
        var uncertainPart = markup[uncertainHeading..];
        Assert.Contains("Lückenserie", certainPart);
        Assert.Contains("11 von 14 Folgen vorhanden", certainPart);
        Assert.Contains("Staffel 1: Folgen 3, 5–6", certainPart);
        Assert.Contains("Staffel 2 komplett (2 Folgen)", certainPart);
        Assert.DoesNotContain("Anime", certainPart);
        Assert.Contains("Anime", uncertainPart);
        Assert.Contains("Staffel 1: Folgen 11–12", uncertainPart);
        Assert.DoesNotContain("Lückenserie", uncertainPart);
        Assert.DoesNotContain("Keine fehlenden Folgen gefunden", markup);
    }

    [Fact]
    public void UncertainSeriesWithoutEpisodeList_ShowCountAndHint()
    {
        SeriesInLibrary(1);
        Add("One Piece", GapStatus.UncertainNumbering, 1180, 61);

        var page = OpenPage();

        Assert.Contains("61 von 1180 Folgen vorhanden", page.Markup);
        Assert.Contains("Genaue Folgen lassen sich bei dieser Zählung nicht bestimmen", page.Markup);
    }

    [Fact]
    public void ShowsHowManySeriesWereCheckedSoFar_AndIgnoresFailedAttempts()
    {
        SeriesInLibrary(5);
        Add("Geprüft", GapStatus.Complete, 4, 4);
        Add("Auch geprüft", GapStatus.Complete, 4, 4);
        Add("Nur Fehlversuch", GapStatus.Complete, 0, 0, hasResult: false);

        var page = OpenPage();

        Assert.Contains("2 von 5 Serien geprüft", page.Markup);
        Assert.Contains("der Rest folgt in den nächsten Läufen", page.Markup);
    }

    [Fact]
    public void NeverShowsMoreCheckedThanTotalSeries_EvenIfTheLibraryIsNotLoadedOrSeriesVanished()
    {
        SeriesInLibrary(1);
        Add("Eins", GapStatus.Complete, 2, 2);
        Add("Zwei", GapStatus.Complete, 2, 2);
        Add("Drei", GapStatus.Complete, 2, 2);

        var page = OpenPage();

        Assert.Contains("3 von 3 Serien geprüft", page.Markup);
        Assert.DoesNotContain("der Rest folgt", page.Markup);
    }

    [Fact]
    public void SeriesWithoutAValidTmdbId_DoNotCountAsCheckable()
    {
        _library.Update(
        [
            JellyfinFixtures.LibraryItem("a", "Gültig", MediaKind.Series, tmdb: "100"),
            JellyfinFixtures.LibraryItem("b", "Null", MediaKind.Series, tmdb: "0"),
            JellyfinFixtures.LibraryItem("c", "Text", MediaKind.Series, tmdb: "abc"),
            JellyfinFixtures.LibraryItem("d", "Film", MediaKind.Movie, tmdb: "5")
        ], DateTime.UtcNow);

        var page = OpenPage();

        Assert.Contains("0 von 1 Serien geprüft", page.Markup);
    }

    [Fact]
    public void ScanButton_IsAvailableWhenNothingRuns()
    {
        SeriesInLibrary(2);

        var page = OpenPage();

        var button = ScanButton(page);
        Assert.NotNull(button);
        Assert.Contains("Alle jetzt prüfen", button!.TextContent);
        Assert.False(button.HasAttribute("disabled"));
    }

    [Fact]
    public void ClickingTheScanButton_RequestsAFullScan_AndWaitsForTheNextRun()
    {
        SeriesInLibrary(2);
        var page = OpenPage();

        ScanButton(page)!.Click();

        Assert.True(_progress.Snapshot.ScanRequested);
        page.WaitForAssertion(() =>
        {
            Assert.True(ScanButton(page)!.HasAttribute("disabled"));
            Assert.Contains("Wartet auf den nächsten Lauf", page.Markup);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WhileARunIsGoing_ThePageShowsHowFarItIs_AndReloadsTheResultsWhenItEnds()
    {
        SeriesInLibrary(4);
        var page = OpenPage();
        ScanButton(page)!.Click();

        _progress.BeginRun();
        _progress.SetTotal(4);
        _progress.Advance();
        _progress.Advance();

        page.WaitForAssertion(() => Assert.Contains("2 von 4 Serien bearbeitet", page.Markup), TimeSpan.FromSeconds(5));
        Add("Neu geprüft", GapStatus.Missing, 3, 2, true, new SeasonGap(1, [3], false));
        _progress.Advance();
        _progress.Advance();
        _progress.Finish();

        Snackbars.WaitForAssertion(() => Assert.Contains("Prüfung abgeschlossen.", Snackbars.Markup), TimeSpan.FromSeconds(5));
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Neu geprüft", page.Markup);
            Assert.False(ScanButton(page)!.HasAttribute("disabled"));
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void OpeningThePageDuringARun_ShowsTheProgressRightAway()
    {
        SeriesInLibrary(3);
        _progress.BeginRun();
        _progress.SetTotal(3);
        _progress.Advance();

        var page = OpenPage();

        Assert.Contains("1 von 3 Serien bearbeitet", page.Markup);
        Assert.True(ScanButton(page)!.HasAttribute("disabled"));
    }

    [Fact]
    public void ARequestNoRunPicksUp_EndsWithAHint_InsteadOfWaitingForever()
    {
        SeriesInLibrary(2);
        var page = OpenPage();
        ScanButton(page)!.Click();

        _time.Advance(SeriesGapProgress.RequestLifetime + TimeSpan.FromSeconds(1));

        Snackbars.WaitForAssertion(() => Assert.Contains("Es lief keine Prüfung an", Snackbars.Markup), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("Prüfung abgeschlossen.", Snackbars.Markup);
        page.WaitForAssertion(() => Assert.False(ScanButton(page)!.HasAttribute("disabled")), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LeavingThePageDuringARun_StopsTheWatching_AndShowsNothingAnymore()
    {
        SeriesInLibrary(2);
        var page = OpenPage();
        ScanButton(page)!.Click();
        _progress.BeginRun();
        _progress.SetTotal(2);

        await DisposeComponentsAsync();
        _progress.Finish();
        await Task.Delay(800);

        Assert.Empty(Services.GetRequiredService<MudBlazor.ISnackbar>().ShownSnackbars);
    }

    [Fact]
    public void SortsSeriesNaturally_ByTitle()
    {
        SeriesInLibrary(3);
        Add("Staffelserie 10", GapStatus.Missing, 2, 1, true, new SeasonGap(1, [2], false));
        Add("Staffelserie 2", GapStatus.Missing, 2, 1, true, new SeasonGap(1, [2], false));

        var page = OpenPage();

        var markup = page.Markup;
        Assert.True(markup.IndexOf("Staffelserie 2", StringComparison.Ordinal) < markup.IndexOf("Staffelserie 10", StringComparison.Ordinal));
    }
}
