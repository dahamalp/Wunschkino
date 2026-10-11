using Wunschkino.Services;

namespace Wunschkino.Tests.Services;

public class UserLimitsTests
{
    [Theory]
    [InlineData(null, UserLimits.DefaultWishesPerDay)]
    [InlineData("", UserLimits.DefaultWishesPerDay)]
    [InlineData("kaputt", UserLimits.DefaultWishesPerDay)]
    [InlineData("-5", UserLimits.DefaultWishesPerDay)]
    [InlineData("1.5", UserLimits.DefaultWishesPerDay)]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    [InlineData("1000", 1000)]
    [InlineData("999999", UserLimits.MaxWishesPerDay)]
    public void ParseWishesPerDay_FallsBackToTheDefault_AndCapsHugeValues(string? stored, int expected)
    {
        Assert.Equal(expected, UserLimits.ParseWishesPerDay(stored));
    }

    [Theory]
    [InlineData(null, UserLimits.DefaultSearchesPerMinute)]
    [InlineData("abc", UserLimits.DefaultSearchesPerMinute)]
    [InlineData("0", 0)]
    [InlineData("30", 30)]
    [InlineData("100000", UserLimits.MaxSearchesPerMinute)]
    public void ParseSearchesPerMinute_FallsBackToTheDefault_AndCapsHugeValues(string? stored, int expected)
    {
        Assert.Equal(expected, UserLimits.ParseSearchesPerMinute(stored));
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(20, 20)]
    [InlineData(5000, UserLimits.MaxWishesPerDay)]
    public void ClampWishesPerDay_KeepsInputInRange(int value, int expected)
    {
        Assert.Equal(expected, UserLimits.ClampWishesPerDay(value));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(15, 15)]
    [InlineData(500, UserLimits.MaxSearchesPerMinute)]
    public void ClampSearchesPerMinute_KeepsInputInRange(int value, int expected)
    {
        Assert.Equal(expected, UserLimits.ClampSearchesPerMinute(value));
    }
}
