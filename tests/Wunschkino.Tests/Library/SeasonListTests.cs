using Wunschkino.Data;
using Wunschkino.Gaps;
using Wunschkino.Library;

namespace Wunschkino.Tests.Library;

public class SeasonListTests
{
    private static SeriesGap Gap(GapStatus status, params SeasonGap[] seasons) => new()
    {
        JellyfinId = "s1",
        Title = "Serie",
        HasResult = true,
        Status = status,
        MissingJson = SeriesGapSerializer.Serialize(seasons)
    };

    [Fact]
    public void GroupsBySeason_SortsEpisodes_AndPutsSpecialsLast()
    {
        var seasons = SeasonList.Build(
        [
            new JellyfinEpisode(2, 1, null, "Neuanfang"),
            new JellyfinEpisode(0, 1, null, "Hinter den Kulissen"),
            new JellyfinEpisode(1, 3, 4, "Doppelfolge"),
            new JellyfinEpisode(1, 1, null, "Pilot"),
            new JellyfinEpisode(1, 1, null, "Pilot (zweite Fassung)")
        ], gap: null);

        Assert.Equal([1, 2, 0], seasons.Select(s => s.Season));
        Assert.Equal([new EpisodeEntry(1, null, "Pilot"), new EpisodeEntry(3, 4, "Doppelfolge")], seasons[0].Episodes);
        Assert.True(seasons[2].IsSpecials);
        Assert.All(seasons, s => Assert.Empty(s.Missing));
    }

    [Fact]
    public void AddsMissingEpisodes_IncludingASeasonThatIsMissingCompletely()
    {
        var seasons = SeasonList.Build(
            [new JellyfinEpisode(1, 1, null, "Pilot"), new JellyfinEpisode(1, 3, null, "Drei")],
            Gap(GapStatus.Missing, new SeasonGap(1, [2], false), new SeasonGap(2, [1, 2], true)));

        Assert.Equal([1, 2], seasons.Select(s => s.Season));
        Assert.Equal([2], seasons[0].Missing);
        Assert.Empty(seasons[1].Episodes);
        Assert.Equal([1, 2], seasons[1].Missing);
    }

    [Fact]
    public void IgnoresGapResults_ThatAreCompleteOrNeverSucceeded()
    {
        var complete = Gap(GapStatus.Complete, new SeasonGap(1, [2], false));
        var neverChecked = Gap(GapStatus.Missing, new SeasonGap(1, [2], false));
        neverChecked.HasResult = false;

        Assert.All([complete, neverChecked], gap =>
            Assert.Empty(SeasonList.Build([new JellyfinEpisode(1, 1, null, "Pilot")], gap).Single().Missing));
    }

    [Fact]
    public void AMissingEpisodeThatIsInJellyfinByNow_IsNotListedAsMissing()
    {
        var seasons = SeasonList.Build(
            [new JellyfinEpisode(1, 1, 2, "Doppelfolge"), new JellyfinEpisode(1, 3, null, "Drei")],
            Gap(GapStatus.Missing, new SeasonGap(1, [2, 3, 4], false)));

        Assert.Equal([4], seasons.Single().Missing);
    }
}
