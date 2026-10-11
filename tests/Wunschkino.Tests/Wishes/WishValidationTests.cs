using Wunschkino.Wishes;

namespace Wunschkino.Tests.Wishes;

public class WishValidationTests
{
    [Theory]
    [InlineData("tt0133093", true)]
    [InlineData("TT0133093", true)]
    [InlineData("tt12345678", true)]
    [InlineData("tt123", false)]
    [InlineData("0133093", false)]
    [InlineData("tt0133093 ", false)]
    [InlineData("matrix tt0133093", false)]
    public void IsImdbId_AcceptsOnlyWellFormedNumbers(string query, bool expected) =>
        Assert.Equal(expected, WishValidation.IsImdbId(query));

    [Fact]
    public void NormalizeText_TrimsAndStripsControlCharacters()
    {
        var result = WishValidation.NormalizeText("  Bitte\r\nbald\t! ", 50, out var tooLong);

        Assert.Equal("Bitte bald !", result);
        Assert.False(tooLong);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeText_ReturnsNullForEmptyInput(string? text)
    {
        Assert.Null(WishValidation.NormalizeText(text, 50, out var tooLong));
        Assert.False(tooLong);
    }

    [Fact]
    public void NormalizeText_FlagsTextsOverTheLimit()
    {
        Assert.Null(WishValidation.NormalizeText(new string('x', 11), 10, out var tooLong));
        Assert.True(tooLong);
        Assert.Equal(new string('x', 10), WishValidation.NormalizeText(new string('x', 10), 10, out tooLong));
        Assert.False(tooLong);
    }
}
