using Wunschkino.Gaps;

namespace Wunschkino.Tests.Gaps;

public class SeriesGapSerializerTests
{
    [Fact]
    public void RoundTrip_KeepsSeasonsAndEpisodes()
    {
        var seasons = new[] { new SeasonGap(1, [2, 3], false), new SeasonGap(2, [1, 2, 3], true) };

        var restored = SeriesGapSerializer.Deserialize(SeriesGapSerializer.Serialize(seasons));

        Assert.Equal(2, restored.Count);
        Assert.Equal([2, 3], restored[0].MissingEpisodes);
        Assert.True(restored[1].WholeSeasonMissing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kein json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void EmptyOrBrokenJson_GivesAnEmptyList(string? json)
    {
        Assert.Empty(SeriesGapSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[null]")]
    [InlineData("""[{"Season":1}]""")]
    public void EntriesWithoutAnEpisodeList_AreDropped_InsteadOfFailingLater(string json)
    {
        var restored = SeriesGapSerializer.Deserialize(json);

        Assert.Empty(restored);
        Assert.Empty(MissingEpisodesText.DescribeAll(restored));
    }

    [Fact]
    public void ValidEntriesNextToBrokenOnes_SurviveTheFiltering()
    {
        var restored = SeriesGapSerializer.Deserialize("""[{}, {"Season":2,"MissingEpisodes":[4],"WholeSeasonMissing":false}]""");

        var season = Assert.Single(restored);
        Assert.Equal(2, season.Season);
        Assert.Equal([4], season.MissingEpisodes);
    }
}
