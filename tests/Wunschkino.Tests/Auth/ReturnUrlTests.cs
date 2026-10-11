using Wunschkino.Auth;

namespace Wunschkino.Tests.Auth;

public class ReturnUrlTests
{
    [Theory]
    [InlineData("/admin/users", "/admin/users")]
    [InlineData("/wuensche?filter=offen", "/wuensche?filter=offen")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://boese.example", "/")]
    [InlineData("//boese.example", "/")]
    [InlineData("/\\boese.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/login?returnUrl=/x", "/")]
    [InlineData("/\t/boese.example", "/")]
    [InlineData("/\n/boese.example", "/")]
    [InlineData("/pfad\\boese", "/")]
    public void Sanitize_AllowsOnlyLocalPaths(string? input, string expected)
    {
        Assert.Equal(expected, ReturnUrl.Sanitize(input));
    }
}
