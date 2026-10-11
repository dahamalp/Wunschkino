using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Gaps;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Gaps;

public partial class SeriesGapServiceTests : IAsyncLifetime, IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AnimeIdMapping _mapping = new();
    private readonly SeriesGapService _service;
    private readonly SeriesGapProgress _progress;

    private readonly Dictionary<string, List<(int Season, int Episode)>> _jellyfinEpisodes = [];
    private readonly Dictionary<int, (int Seasons, int EpisodesPerSeason)> _tmdbSeries = [];
    private readonly HashSet<int> _tmdbFailing = [];
    private readonly List<string> _jellyfinPaths = [];
    private readonly List<string> _tmdbPaths = [];

    public SeriesGapServiceTests()
    {
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _progress = new SeriesGapProgress(_time);
        _service = new SeriesGapService(
            _db,
            _settings,
            new JellyfinClient(new HttpClient(new FakeHttpHandler(JellyfinResponse))),
            new TmdbClient(new HttpClient(new FakeHttpHandler(TmdbResponse))),
            _mapping,
            _progress,
            _time,
            NullLogger<SeriesGapService>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _settings.SetAsync(SettingKeys.JellyfinUrl, "http://jellyfin.test:8096", isProtected: false);
        await _settings.SetAsync(SettingKeys.JellyfinApiKey, "jfkey", isProtected: true);
        await _settings.SetAsync(SettingKeys.TmdbApiKey, "v3key", isProtected: true);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private HttpResponseMessage JellyfinResponse(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        _jellyfinPaths.Add(path);
        var id = Uri.UnescapeDataString(path.Split('/')[2]);
        var items = _jellyfinEpisodes.GetValueOrDefault(id) ?? [];
        var json = string.Join(',', items.Select(e => $"{{\"ParentIndexNumber\":{e.Season},\"IndexNumber\":{e.Episode},\"LocationType\":\"FileSystem\"}}"));
        return Json("{\"Items\":[" + json + "]}");
    }

    private HttpResponseMessage TmdbResponse(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        _tmdbPaths.Add(path);
        var seasonMatch = SeasonPath().Match(path);
        var seriesMatch = SeriesPath().Match(path);
        var tmdbId = int.Parse((seasonMatch.Success ? seasonMatch : seriesMatch).Groups[1].Value);

        if (_tmdbFailing.Contains(tmdbId))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        if (!_tmdbSeries.TryGetValue(tmdbId, out var series))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        if (seasonMatch.Success)
        {
            var episodes = string.Join(',', Enumerable.Range(1, series.EpisodesPerSeason).Select(n => $"{{\"episode_number\":{n},\"air_date\":\"2011-01-01\"}}"));
            return Json("{\"episodes\":[" + episodes + "]}");
        }
        var seasons = string.Join(',', Enumerable.Range(1, series.Seasons).Select(n => $"{{\"season_number\":{n}}}"));
        return Json("{\"seasons\":[" + seasons + "]}");
    }

    [GeneratedRegex(@"^/3/tv/(\d+)/season/\d+$")]
    private static partial Regex SeasonPath();

    [GeneratedRegex(@"^/3/tv/(\d+)$")]
    private static partial Regex SeriesPath();

    private static LibraryItem Show(string jellyfinId, string title, int tmdbId, string? aniListId = null)
    {
        var providers = new Dictionary<string, string> { ["Tmdb"] = tmdbId.ToString() };
        if (aniListId is not null)
            providers["AniList"] = aniListId;
        return new LibraryItem(jellyfinId, title, MediaKind.Series, 2011, null, true, providers);
    }

    private void Tmdb(int tmdbId, int seasons, int episodesPerSeason) => _tmdbSeries[tmdbId] = (seasons, episodesPerSeason);

    private void Has(string jellyfinId, int season, params int[] episodes) =>
        _jellyfinEpisodes[jellyfinId] = [.. _jellyfinEpisodes.GetValueOrDefault(jellyfinId) ?? [], .. episodes.Select(e => (season, e))];

    private SeriesGap Row(string jellyfinId)
    {
        using var db = _db.CreateDbContext();
        return db.SeriesGaps.Single(g => g.JellyfinId == jellyfinId);
    }

    [Fact]
    public async Task CheckDue_StoresTheOutcomePerSeries()
    {
        Tmdb(100, seasons: 1, episodesPerSeason: 4);
        Tmdb(200, seasons: 1, episodesPerSeason: 2);
        Has("a", 1, 1, 2, 4);
        Has("b", 1, 1, 2);

        var checkedCount = await _service.CheckDueAsync([Show("a", "Lückenserie", 100), Show("b", "Vollständig", 200)]);

        Assert.Equal(2, checkedCount);
        var gap = Row("a");
        Assert.True(gap.HasResult);
        Assert.Equal(GapStatus.Missing, gap.Status);
        Assert.Equal(4, gap.ExpectedCount);
        Assert.Equal(3, gap.PresentCount);
        var missing = Assert.Single(SeriesGapSerializer.Deserialize(gap.MissingJson));
        Assert.Equal([3], missing.MissingEpisodes);
        Assert.Equal(GapStatus.Complete, Row("b").Status);
    }

    [Fact]
    public async Task CheckDue_IgnoresMoviesAndSeriesWithoutTmdbId()
    {
        Tmdb(100, 1, 2);
        var movie = new LibraryItem("m", "Film", MediaKind.Movie, 2000, null, true, new Dictionary<string, string> { ["Tmdb"] = "100" });
        var noId = new LibraryItem("n", "Ohne ID", MediaKind.Series, 2000, null, true, new Dictionary<string, string> { ["Imdb"] = "tt0000001" });

        var checkedCount = await _service.CheckDueAsync([movie, noId]);

        Assert.Equal(0, checkedCount);
        Assert.Empty(_jellyfinPaths);
        Assert.Empty(_tmdbPaths);
    }

    [Fact]
    public async Task CheckDue_DoesNotCheckAgainBeforeTheIntervalHasPassed()
    {
        Tmdb(100, 1, 2);
        Has("a", 1, 1, 2);
        var library = new[] { Show("a", "Serie", 100) };
        await _service.CheckDueAsync(library);
        var requestsAfterFirstRun = _jellyfinPaths.Count;

        _time.Advance(SeriesGapService.Interval - TimeSpan.FromMinutes(1));
        var early = await _service.CheckDueAsync(library);
        _time.Advance(TimeSpan.FromMinutes(2));
        var late = await _service.CheckDueAsync(library);

        Assert.Equal(0, early);
        Assert.Equal(requestsAfterFirstRun + 1, _jellyfinPaths.Count);
        Assert.Equal(1, late);
    }

    [Fact]
    public async Task CheckDue_PicksUpNewEpisodesOnTheNextCheck_AndRefreshesTheTitle()
    {
        Tmdb(100, 1, 3);
        Has("a", 1, 1);
        await _service.CheckDueAsync([Show("a", "Alter Titel", 100)]);
        Assert.Equal(GapStatus.Missing, Row("a").Status);

        Has("a", 1, 2, 3);
        _time.Advance(SeriesGapService.Interval + TimeSpan.FromMinutes(1));
        await _service.CheckDueAsync([Show("a", "Neuer Titel", 100)]);

        var row = Row("a");
        Assert.Equal(GapStatus.Complete, row.Status);
        Assert.Equal("Neuer Titel", row.Title);
    }

    [Fact]
    public async Task CheckDue_RemovesRowsOfSeriesThatLeftTheLibrary_ButKeepsEverythingWhenTheLibraryIsEmpty()
    {
        Tmdb(100, 1, 1);
        Tmdb(200, 1, 1);
        await _service.CheckDueAsync([Show("a", "A", 100), Show("b", "B", 200)]);

        await _service.CheckDueAsync([]);
        using (var db = _db.CreateDbContext())
            Assert.Equal(2, db.SeriesGaps.Count());

        await _service.CheckDueAsync([Show("a", "A", 100)]);
        using (var db = _db.CreateDbContext())
            Assert.Equal(["a"], db.SeriesGaps.Select(g => g.JellyfinId).ToList());
    }

    [Fact]
    public async Task CheckDue_ASeriesUnknownToTmdb_IsStoredAsCompleteWithoutResultsToCompare()
    {
        Has("a", 1, 1, 2);

        await _service.CheckDueAsync([Show("a", "Unbekannt", 999)]);

        var row = Row("a");
        Assert.True(row.HasResult);
        Assert.Equal(GapStatus.Complete, row.Status);
        Assert.Equal(0, row.ExpectedCount);
    }

    [Fact]
    public async Task CheckDue_AnimeWithAGap_IsMarkedUncertain()
    {
        Tmdb(100, 1, 4);
        Tmdb(200, 1, 4);
        Has("plugin", 1, 1, 2);
        Has("mapped", 1, 1, 2);
        _mapping.Replace(new Dictionary<int, AnimeMapping> { [55] = new(55, MediaKind.Series, 200, []) }, DateTime.UtcNow);

        await _service.CheckDueAsync([Show("plugin", "Per Plugin", 100, aniListId: "9"), Show("mapped", "Per Liste", 200)]);

        Assert.Equal(GapStatus.UncertainNumbering, Row("plugin").Status);
        Assert.Equal(GapStatus.UncertainNumbering, Row("mapped").Status);
    }

    [Fact]
    public async Task CheckDue_AFailingSeries_DoesNotBlockTheOthers_AndIsRetriedAfterSixHours()
    {
        Tmdb(100, 1, 2);
        Tmdb(200, 1, 2);
        _tmdbFailing.Add(100);
        Has("good", 1, 1, 2);
        var library = new[] { Show("bad", "Defekt", 100), Show("good", "Gut", 200) };

        var checkedCount = await _service.CheckDueAsync(library);

        Assert.Equal(1, checkedCount);
        Assert.False(Row("bad").HasResult);
        Assert.True(Row("good").HasResult);

        _tmdbFailing.Clear();
        Has("bad", 1, 1, 2);
        _time.Advance(SeriesGapService.RetryAfterFailure - TimeSpan.FromMinutes(1));
        Assert.Equal(0, await _service.CheckDueAsync(library));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await _service.CheckDueAsync(library));
        Assert.True(Row("bad").HasResult);
    }

    [Fact]
    public async Task CheckDue_ADuplicateJellyfinEntry_DoesNotAbortTheRun()
    {
        Tmdb(100, 1, 2);
        Tmdb(200, 1, 2);
        Has("a", 1, 1, 2);
        Has("b", 1, 1, 2);

        var checkedCount = await _service.CheckDueAsync([Show("a", "Doppelt", 100), Show("a", "Doppelt", 100), Show("b", "Danach", 200)]);

        Assert.Equal(2, checkedCount);
        using var db = _db.CreateDbContext();
        Assert.Equal(["a", "b"], db.SeriesGaps.Select(g => g.JellyfinId).OrderBy(id => id).ToList());
        Assert.All(db.SeriesGaps, g => Assert.True(g.HasResult));
    }

    [Fact]
    public async Task CheckDue_StopsAfterThreeFailuresInARow()
    {
        var library = new List<LibraryItem>();
        for (var i = 1; i <= 6; i++)
        {
            Tmdb(i, 1, 1);
            _tmdbFailing.Add(i);
            library.Add(Show($"s{i}", $"Serie {i}", i));
        }

        var checkedCount = await _service.CheckDueAsync(library);

        Assert.Equal(0, checkedCount);
        Assert.Equal(3, _jellyfinPaths.Count);
    }

    private List<LibraryItem> ThirtySeries()
    {
        var library = new List<LibraryItem>();
        for (var i = 1; i <= 30; i++)
        {
            Tmdb(i, 1, 1);
            library.Add(Show($"s{i:00}", $"Serie {i}", i));
        }
        return library;
    }

    [Fact]
    public async Task CheckDue_TheFirstFill_ChecksEverySeriesNeverCheckedInOneRun()
    {
        var library = ThirtySeries();

        var first = await _service.CheckDueAsync(library);
        var second = await _service.CheckDueAsync(library);

        Assert.Equal(30, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task CheckDue_InRegularUpkeep_HandlesAtMostTwentyOverdueSeriesPerRun()
    {
        var library = ThirtySeries();
        await _service.CheckDueAsync(library);
        _time.Advance(SeriesGapService.Interval + TimeSpan.FromMinutes(1));

        var first = await _service.CheckDueAsync(library);
        var second = await _service.CheckDueAsync(library);
        var third = await _service.CheckDueAsync(library);

        Assert.Equal(20, first);
        Assert.Equal(10, second);
        Assert.Equal(0, third);
    }

    [Fact]
    public async Task CheckDue_NewSeriesAreAddedAtOnce_NextToTheCappedUpkeep()
    {
        var library = ThirtySeries();
        await _service.CheckDueAsync(library);
        _time.Advance(SeriesGapService.Interval + TimeSpan.FromMinutes(1));
        for (var i = 31; i <= 70; i++)
        {
            Tmdb(i, 1, 1);
            library.Add(Show($"s{i:00}", $"Serie {i}", i));
        }

        var checkedCount = await _service.CheckDueAsync(library);

        Assert.Equal(40 + 20, checkedCount);
    }

    [Fact]
    public async Task CheckDue_AFullScan_ChecksEverySeriesAgain_EvenIfNotDue_AndOnlyOnce()
    {
        var library = ThirtySeries();
        await _service.CheckDueAsync(library);
        var requestsAfterFirstRun = _tmdbPaths.Count;

        _progress.RequestFullScan();
        var scanned = await _service.CheckDueAsync(library);
        var afterwards = await _service.CheckDueAsync(library);

        Assert.Equal(30, scanned);
        Assert.Equal(requestsAfterFirstRun * 2, _tmdbPaths.Count);
        Assert.Equal(0, afterwards);
    }

    [Fact]
    public async Task CheckDue_ReportsProgress_AndIsIdleAfterwards()
    {
        var library = ThirtySeries();

        await _service.CheckDueAsync(library);

        var snapshot = _progress.Snapshot;
        Assert.False(snapshot.Running);
        Assert.False(snapshot.Busy);
        Assert.Equal((30, 30), (snapshot.Total, snapshot.Done));
    }

    [Fact]
    public async Task CheckDue_FinishesTheProgress_EvenWhenThereIsNothingToDo()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Settings.RemoveRange(db.Settings);
            db.SaveChanges();
        }
        _progress.RequestFullScan();

        await _service.CheckDueAsync([Show("a", "Serie", 100)]);

        Assert.False(_progress.Snapshot.Busy);
    }

    [Fact]
    public async Task CheckDue_ProgressCountsFailedSeriesToo_WhenTheRunStopsEarly()
    {
        var library = new List<LibraryItem>();
        for (var i = 1; i <= 6; i++)
        {
            Tmdb(i, 1, 1);
            _tmdbFailing.Add(i);
            library.Add(Show($"s{i}", $"Serie {i}", i));
        }

        await _service.CheckDueAsync(library);

        var snapshot = _progress.Snapshot;
        Assert.Equal(6, snapshot.Total);
        Assert.Equal(3, snapshot.Done);
        Assert.False(snapshot.Running);
    }

    [Fact]
    public async Task CheckDue_WithoutSettings_DoesNothing()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Settings.RemoveRange(db.Settings);
            db.SaveChanges();
        }
        Tmdb(100, 1, 1);

        var checkedCount = await _service.CheckDueAsync([Show("a", "Serie", 100)]);

        Assert.Equal(0, checkedCount);
        Assert.Empty(_jellyfinPaths);
    }
}
