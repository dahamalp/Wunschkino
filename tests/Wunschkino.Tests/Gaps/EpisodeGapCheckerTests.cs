using Wunschkino.Gaps;

namespace Wunschkino.Tests.Gaps;

public class EpisodeGapCheckerTests
{
    private static readonly DateOnly Today = new(2026, 10, 3);

    private static TmdbEpisode Aired(int season, int number, int daysAgo = 30) =>
        new(season, number, Today.AddDays(-daysAgo));

    private static IEnumerable<TmdbEpisode> Season(int season, int count) =>
        Enumerable.Range(1, count).Select(n => Aired(season, n));

    private static IEnumerable<JellyfinEpisode> Have(int season, params int[] numbers) =>
        numbers.Select(n => new JellyfinEpisode(season, n, null));

    private static GapResult Compare(IEnumerable<TmdbEpisode> expected, IEnumerable<JellyfinEpisode> present, bool numberingMayDiffer = false) =>
        EpisodeGapChecker.Compare(expected.ToList(), present.ToList(), Today, numberingMayDiffer);

    [Fact]
    public void EverythingPresent_IsComplete()
    {
        var result = Compare(Season(1, 3), Have(1, 1, 2, 3));

        Assert.Equal(GapStatus.Complete, result.Status);
        Assert.Empty(result.Seasons);
        Assert.Equal(3, result.ExpectedCount);
        Assert.Equal(3, result.PresentCount);
    }

    [Fact]
    public void SingleMissingEpisode_IsReportedWithItsNumber()
    {
        var result = Compare(Season(1, 4), Have(1, 1, 2, 4));

        Assert.Equal(GapStatus.Missing, result.Status);
        var season = Assert.Single(result.Seasons);
        Assert.Equal(1, season.Season);
        Assert.Equal([3], season.MissingEpisodes);
        Assert.False(season.WholeSeasonMissing);
        Assert.Equal(4, result.ExpectedCount);
        Assert.Equal(3, result.PresentCount);
    }

    [Fact]
    public void WholeSeasonMissing_IsFlagged()
    {
        var result = Compare(Season(1, 2).Concat(Season(2, 3)), Have(1, 1, 2));

        Assert.Equal(GapStatus.Missing, result.Status);
        var season = Assert.Single(result.Seasons);
        Assert.Equal(2, season.Season);
        Assert.True(season.WholeSeasonMissing);
        Assert.Equal([1, 2, 3], season.MissingEpisodes);
    }

    [Fact]
    public void SpecialsInSeasonZero_AreIgnoredOnBothSides()
    {
        var expected = Season(0, 3).Concat(Season(1, 2));

        var result = Compare(expected, Have(1, 1, 2));

        Assert.Equal(GapStatus.Complete, result.Status);
        Assert.Equal(2, result.ExpectedCount);
    }

    [Fact]
    public void UnairedEpisodes_DoNotCount()
    {
        var expected = Season(1, 2).Append(new TmdbEpisode(1, 3, Today.AddDays(7))).Append(new TmdbEpisode(1, 4, null));

        var result = Compare(expected, Have(1, 1, 2));

        Assert.Equal(GapStatus.Complete, result.Status);
        Assert.Equal(2, result.ExpectedCount);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void FreshlyAiredEpisodes_AreGivenGraceTime(int daysAgo, bool stillWithinGrace)
    {
        var expected = Season(1, 1).Append(Aired(1, 2, daysAgo));

        var result = Compare(expected, Have(1, 1));

        Assert.Equal(stillWithinGrace ? GapStatus.Complete : GapStatus.Missing, result.Status);
    }

    [Fact]
    public void DoubleEpisodeFile_CoversItsWholeRange()
    {
        var present = new[] { new JellyfinEpisode(1, 1, null), new JellyfinEpisode(1, 2, 3), new JellyfinEpisode(1, 4, null) };

        var result = Compare(Season(1, 4), present);

        Assert.Equal(GapStatus.Complete, result.Status);
    }

    [Fact]
    public void AbsurdEpisodeRanges_AreCapped_InsteadOfAllocatingEndlessly()
    {
        var present = new[] { new JellyfinEpisode(1, 1, int.MaxValue) };

        var result = Compare(Season(1, 60), present);

        Assert.Equal(GapStatus.Missing, result.Status);
        Assert.Equal(Enumerable.Range(51, 10), Assert.Single(result.Seasons).MissingEpisodes);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 10)]
    [InlineData(0)]
    [InlineData(-5)]
    public void ImplausibleEpisodeNumbersInJellyfin_AreIgnored_WithoutHangingOrOverflowing(int number)
    {
        var present = Have(1, 1, 2).Append(new JellyfinEpisode(1, number, int.MaxValue));

        var result = EpisodeGapChecker.Compare(Season(1, 3).ToList(), present.ToList(), Today, numberingMayDiffer: false);

        Assert.Equal(GapStatus.Missing, result.Status);
        Assert.Equal([3], Assert.Single(result.Seasons).MissingEpisodes);
    }

    [Fact]
    public void JellyfinHasASeasonTmdbDoesNotKnow_AndStillLacksEpisodes_NumberingIsUncertain()
    {
        var present = Have(1, 1).Concat(Have(2, 1, 2));

        var result = Compare(Season(1, 5), present);

        Assert.Equal(GapStatus.UncertainNumbering, result.Status);
    }

    // Naruto: Shippuden/One Piece: TMDB hat viele Staffeln mit durchgehender Folgennummer, Jellyfin eine Staffel.
    private static IEnumerable<TmdbEpisode> ContinuousAnime(params int[] seasonLengths)
    {
        var number = 1;
        for (var season = 1; season <= seasonLengths.Length; season++)
            for (var i = 0; i < seasonLengths[season - 1]; i++)
                yield return Aired(season, number++);
    }

    [Fact]
    public void ContinuousNumbering_OneJellyfinSeasonWithAllEpisodes_IsComplete()
    {
        var result = Compare(ContinuousAnime(20, 20, 20), Have(1, Enumerable.Range(1, 60).ToArray()), numberingMayDiffer: true);

        Assert.Equal(GapStatus.Complete, result.Status);
        Assert.Empty(result.Seasons);
        Assert.Equal(60, result.ExpectedCount);
        Assert.Equal(60, result.PresentCount);
    }

    [Fact]
    public void ContinuousNumbering_RealGaps_AreFoundByNumberAndGroupedByTmdbSeason()
    {
        var present = Have(1, Enumerable.Range(1, 60).Except([5, 45]).ToArray());

        var result = Compare(ContinuousAnime(20, 20, 20), present, numberingMayDiffer: true);

        Assert.Equal(GapStatus.UncertainNumbering, result.Status);
        Assert.Equal([1, 3], result.Seasons.Select(s => s.Season));
        Assert.Equal([5], result.Seasons[0].MissingEpisodes);
        Assert.Equal([45], result.Seasons[1].MissingEpisodes);
        Assert.Equal(58, result.PresentCount);
    }

    [Fact]
    public void ContinuousTmdbNumbering_JellyfinRestartsPerSeason_ComparesByCount()
    {
        var present = Have(1, Enumerable.Range(1, 20).ToArray()).Concat(Have(2, Enumerable.Range(1, 20).ToArray())).Concat(Have(3, Enumerable.Range(1, 20).ToArray()));

        var result = Compare(ContinuousAnime(20, 20, 20), present, numberingMayDiffer: true);

        Assert.Equal(GapStatus.Complete, result.Status);
    }

    [Fact]
    public void ContinuousTmdbNumbering_JellyfinRestartsPerSeason_ButIncomplete_ReportsOnlyTheCount()
    {
        var present = Have(1, Enumerable.Range(1, 20).ToArray()).Concat(Have(2, Enumerable.Range(1, 15).ToArray()));

        var result = Compare(ContinuousAnime(20, 20, 20), present, numberingMayDiffer: true);

        Assert.Equal(GapStatus.UncertainNumbering, result.Status);
        Assert.Empty(result.Seasons);
        Assert.Equal(35, result.PresentCount);
    }

    [Fact]
    public void ContinuousNumbering_WorksWithoutAnimeFlag_WhenJellyfinHasForeignNumbers()
    {
        var result = Compare(ContinuousAnime(20, 20), Have(1, Enumerable.Range(1, 40).ToArray()));

        Assert.Equal(GapStatus.Complete, result.Status);
    }

    [Fact]
    public void RestartingNumbers_MoreEpisodesInJellyfinThanTmdbAired_IsComplete()
    {
        var present = Have(1, Enumerable.Range(1, 25).ToArray()).Concat(Have(2, 1, 2, 3));

        var result = Compare(Season(1, 12).Concat(Season(2, 12)), present, numberingMayDiffer: true);

        Assert.Equal(GapStatus.Complete, result.Status);
        Assert.Empty(result.Seasons);
        Assert.Equal(24, result.PresentCount);
    }

    [Fact]
    public void RestartingNumbers_ExactlyAsManyEpisodesAsTmdbAired_IsComplete()
    {
        var result = Compare(Season(1, 12).Concat(Season(2, 12)), Have(1, Enumerable.Range(1, 24).ToArray()), numberingMayDiffer: true);

        Assert.Equal(GapStatus.Complete, result.Status);
    }

    [Fact]
    public void RestartingNumbers_JellyfinMatchesTmdbSeasons_ListsTheMissingSeasonAsUncertain()
    {
        var result = Compare(Season(1, 12).Concat(Season(2, 12)), Have(1, Enumerable.Range(1, 12).ToArray()), numberingMayDiffer: true);

        Assert.Equal(GapStatus.UncertainNumbering, result.Status);
        var season = Assert.Single(result.Seasons);
        Assert.Equal(2, season.Season);
        Assert.True(season.WholeSeasonMissing);
        Assert.Equal(12, result.PresentCount);
    }

    [Fact]
    public void RestartingNumbers_FewerEpisodesThanTmdb_ReportsOnlyTheCount()
    {
        var present = Have(1, Enumerable.Range(1, 30).ToArray());

        var result = Compare(Season(1, 20).Concat(Season(2, 20)), present, numberingMayDiffer: true);

        Assert.Equal(GapStatus.UncertainNumbering, result.Status);
        Assert.Empty(result.Seasons);
        Assert.Equal(40, result.ExpectedCount);
        Assert.Equal(30, result.PresentCount);
    }

    [Fact]
    public void NormalSeriesWithMatchingNumbering_StaysUnchanged()
    {
        var result = Compare(Season(1, 10).Concat(Season(2, 10)), Have(1, Enumerable.Range(1, 10).ToArray()).Concat(Have(2, 1, 2, 3)));

        Assert.Equal(GapStatus.Missing, result.Status);
        Assert.Equal(7, Assert.Single(result.Seasons).MissingEpisodes.Count);
    }

    [Fact]
    public void JellyfinHasEpisodesBeyondTmdb_NumberingIsUncertain()
    {
        var result = Compare(Season(1, 2), Have(1, 1, 3, 4));

        Assert.Equal(GapStatus.UncertainNumbering, result.Status);
    }

    [Fact]
    public void ExtraSpecialsInJellyfin_AreNotAMismatch()
    {
        var present = Have(0, 1, 2).Concat(Have(1, 1, 2));

        var result = Compare(Season(1, 2), present);

        Assert.Equal(GapStatus.Complete, result.Status);
    }

    [Fact]
    public void AnimeWithPossiblyDifferingNumbering_ReportsGapsAsUncertain_NotAsMissing()
    {
        var result = Compare(Season(1, 4), Have(1, 1, 2), numberingMayDiffer: true);

        Assert.Equal(GapStatus.UncertainNumbering, result.Status);
        Assert.Equal([3, 4], Assert.Single(result.Seasons).MissingEpisodes);
    }

    [Fact]
    public void AnimeWithoutGaps_IsComplete_EvenIfNumberingMayDiffer()
    {
        var result = Compare(Season(1, 2), Have(1, 1, 2), numberingMayDiffer: true);

        Assert.Equal(GapStatus.Complete, result.Status);
    }

    [Fact]
    public void SeriesWithoutAnyAiredEpisode_IsComplete()
    {
        var result = Compare([new TmdbEpisode(1, 1, Today.AddDays(30))], []);

        Assert.Equal(GapStatus.Complete, result.Status);
        Assert.Equal(0, result.ExpectedCount);
    }

    [Fact]
    public void MissingEpisodes_AreSortedBySeasonAndNumber()
    {
        var expected = Season(2, 3).Concat(Season(1, 3));

        var result = Compare(expected, Have(1, 2).Concat(Have(2, 1)));

        Assert.Equal([1, 2], result.Seasons.Select(s => s.Season));
        Assert.Equal([1, 3], result.Seasons[0].MissingEpisodes);
        Assert.Equal([2, 3], result.Seasons[1].MissingEpisodes);
    }
}
