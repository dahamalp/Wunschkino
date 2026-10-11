using Wunschkino.Gaps;

namespace Wunschkino.Tests.Gaps;

public class MissingEpisodesTextTests
{
    [Fact]
    public void WholeSeason_SaysHowManyEpisodesAreMissing()
    {
        Assert.Equal("Staffel 2 komplett (10 Folgen)", MissingEpisodesText.Describe(new SeasonGap(2, Enumerable.Range(1, 10).ToList(), true)));
    }

    [Fact]
    public void WholeSeasonWithOneEpisode_UsesTheSingular()
    {
        Assert.Equal("Staffel 1 komplett (1 Folge)", MissingEpisodesText.Describe(new SeasonGap(1, [1], true)));
    }

    [Fact]
    public void SingleEpisode_UsesTheSingular()
    {
        Assert.Equal("Staffel 1: Folge 3", MissingEpisodesText.Describe(new SeasonGap(1, [3], false)));
    }

    [Fact]
    public void SeveralEpisodes_AreListed_AndRunsAreShortened()
    {
        Assert.Equal("Staffel 3: Folgen 3, 5–6, 9", MissingEpisodesText.Describe(new SeasonGap(3, [3, 5, 6, 9], false)));
    }

    [Fact]
    public void LongRuns_AreShortenedToOneRange()
    {
        Assert.Equal("Staffel 1: Folgen 4–12", MissingEpisodesText.Describe(new SeasonGap(1, Enumerable.Range(4, 9).ToList(), false)));
    }

    [Fact]
    public void UnsortedInput_IsSortedFirst()
    {
        Assert.Equal("Staffel 1: Folgen 2–3, 7", MissingEpisodesText.Describe(new SeasonGap(1, [7, 3, 2], false)));
    }

    [Fact]
    public void DescribeAll_JoinsTheSeasons()
    {
        var text = MissingEpisodesText.DescribeAll([new SeasonGap(1, [2], false), new SeasonGap(2, [1, 2], true)]);

        Assert.Equal(["Staffel 1: Folge 2", "Staffel 2 komplett (2 Folgen)"], text);
    }
}
