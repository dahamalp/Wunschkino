using Wunschkino.Anime;
using Wunschkino.Library;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;

namespace Wunschkino.Tests.Wishes;

public class WishLibraryIndexTests
{
    private static WishLibraryIndex Build(AnimeIdMapping? mapping = null, params LibraryItem[] items) =>
        WishLibraryIndex.Build(items, mapping ?? new AnimeIdMapping());

    [Fact]
    public void Contains_MatchesTmdbIdPerKind()
    {
        var index = Build(items: JellyfinFixtures.LibraryItem("j1", "Film", MediaKind.Movie, tmdb: "1399"));

        Assert.True(index.Contains(MediaKind.Movie, 1399, null));
        Assert.False(index.Contains(MediaKind.Series, 1399, null));
        Assert.False(index.Contains(MediaKind.Movie, 1400, null));
    }

    [Fact]
    public void Contains_MatchesImdbIdIgnoringCase()
    {
        var index = Build(items: JellyfinFixtures.LibraryItem("j1", "Film", imdb: "tt0133093"));

        Assert.True(index.Contains(MediaKind.Movie, 1, "TT0133093"));
        Assert.False(index.Contains(MediaKind.Movie, 1, "tt0000001"));
        Assert.False(index.Contains(MediaKind.Movie, 1, null));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("12.5")]
    [InlineData("")]
    public void Build_IgnoresUnusableProviderIds(string tmdbId)
    {
        var index = Build(items: JellyfinFixtures.LibraryItem("j1", "Film", tmdb: tmdbId));

        Assert.False(index.Contains(MediaKind.Movie, 0, null));
        Assert.False(index.Contains(MediaKind.Movie, 5, null));
    }

    [Fact]
    public void Contains_TranslatesAniListPluginIdsViaTheMappingList()
    {
        var mapping = new AnimeIdMapping();
        mapping.Replace(new Dictionary<int, AnimeMapping>
        {
            [9989] = new(9989, MediaKind.Series, 1399, ["tt0944947"]),
            [888] = new(888, null, null, ["tt0000888"])
        }, DateTime.UtcNow);
        var withPlugin = (string id) => new LibraryItem(id, "Anime", MediaKind.Series, null, null, true, new Dictionary<string, string> { ["AniList"] = id });

        var index = WishLibraryIndex.Build([withPlugin("9989"), withPlugin("888"), withPlugin("777")], mapping);

        Assert.True(index.Contains(MediaKind.Series, 1399, null));
        Assert.True(index.Contains(MediaKind.Movie, 99, "tt0944947"));
        Assert.True(index.Contains(MediaKind.Movie, 99, "tt0000888"));
        Assert.False(index.Contains(MediaKind.Series, 777, null));
    }

    [Fact]
    public void Contains_IsFalseForAnEmptyLibrary() =>
        Assert.False(Build().Contains(MediaKind.Movie, 1, "tt0000001"));
}
