using Wunschkino.Library;
using static Wunschkino.Tests.Helpers.JellyfinFixtures;

namespace Wunschkino.Tests.Library;

public class LibraryQueryTests
{
    private static readonly LibraryItem[] Items =
    [
        LibraryItem("1", "Zoomania", MediaKind.Movie, 2016, added: new DateTime(2026, 1, 1)),
        LibraryItem("2", "Arcane", MediaKind.Series, 2021, added: new DateTime(2026, 3, 1)),
        LibraryItem("3", "Ärger im Paradies", MediaKind.Movie, 1932, added: new DateTime(2026, 2, 1)),
        LibraryItem("4", "Ohne Jahr", MediaKind.Movie)
    ];

    [Fact]
    public void FilterByKind()
    {
        var result = LibraryQuery.Apply(Items, null, MediaKind.Series, LibrarySort.Title);

        Assert.Equal(["2"], result.Select(i => i.JellyfinId));
    }

    [Theory]
    [InlineData("arc", "2")]
    [InlineData(" ÄRGER ", "3")]
    [InlineData("2016", "1")]
    public void SearchMatchesTitleCaseInsensitiveOrExactYear(string search, string expectedId)
    {
        var result = LibraryQuery.Apply(Items, search, null, LibrarySort.Title);

        Assert.Equal([expectedId], result.Select(i => i.JellyfinId));
    }

    [Fact]
    public void SortByTitle_OrdersNumbersNaturally()
    {
        LibraryItem[] items =
        [
            LibraryItem("a", "Staffel 10", MediaKind.Series),
            LibraryItem("b", "Staffel 2", MediaKind.Series),
            LibraryItem("c", "Staffel 1", MediaKind.Series)
        ];

        var result = LibraryQuery.Apply(items, null, null, LibrarySort.Title);

        Assert.Equal(["c", "b", "a"], result.Select(i => i.JellyfinId));
    }

    [Fact]
    public void SortByYearDescending_PutsUnknownYearsLast()
    {
        var result = LibraryQuery.Apply(Items, null, null, LibrarySort.YearDescending);

        Assert.Equal(["2", "1", "3", "4"], result.Select(i => i.JellyfinId));
    }

    [Fact]
    public void SortByRecentlyAdded()
    {
        var result = LibraryQuery.Apply(Items, null, MediaKind.Movie, LibrarySort.RecentlyAdded);

        Assert.Equal(["3", "1", "4"], result.Select(i => i.JellyfinId));
    }
}
