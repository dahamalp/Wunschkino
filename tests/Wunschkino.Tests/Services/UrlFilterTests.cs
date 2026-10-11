using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Wishes;

namespace Wunschkino.Tests.Services;

public class UrlFilterTests
{
    [Theory]
    [InlineData("series", MediaKind.Series)]
    [InlineData("SERIES", MediaKind.Series)]
    [InlineData("movie", MediaKind.Movie)]
    public void ParseEnum_AcceptsNamesInAnyCase(string value, MediaKind expected) =>
        Assert.Equal(expected, UrlFilter.ParseEnum<MediaKind>(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("7")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("series,movie")]
    [InlineData("unbekannt")]
    public void ParseEnum_RejectsEverythingElse(string? value) =>
        Assert.Null(UrlFilter.ParseEnum<MediaKind>(value));

    [Fact]
    public void Write_OmitsTheDefault_AndLowercasesTheRest()
    {
        Assert.Null(UrlFilter.Write<WishScope>(WishScope.All, WishScope.All));
        Assert.Equal("mine", UrlFilter.Write<WishScope>(WishScope.Mine, WishScope.All));
        Assert.Equal("movie", UrlFilter.Write<MediaKind>(MediaKind.Movie));
        Assert.Null(UrlFilter.Write<MediaKind>(null));
    }

    [Fact]
    public void Text_TrimsToTheLimit_AndDropsBlanks()
    {
        Assert.Null(UrlFilter.Text("   "));
        Assert.Null(UrlFilter.Text(null));
        Assert.Equal(UrlFilter.MaxTextLength, UrlFilter.Text(new string('a', 500))!.Length);
        Assert.Equal("matrix", UrlFilter.Text("matrix"));
    }

    [Fact]
    public void Text_NeverCutsASurrogatePairInHalf()
    {
        var value = new string('a', UrlFilter.MaxTextLength - 1) + "😀" + "tail";

        var result = UrlFilter.Text(value)!;

        Assert.Equal(UrlFilter.MaxTextLength - 1, result.Length);
        Assert.False(char.IsHighSurrogate(result[^1]));
    }
}
