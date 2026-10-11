using Wunschkino.Auth;

namespace Wunschkino.Tests.Auth;

public class CommonPasswordsTests
{
    [Fact]
    public void ListIsLoadedFromTheEmbeddedResource() =>
        Assert.True(CommonPasswords.Count > 40_000);

    [Theory]
    [InlineData("password1234")]
    [InlineData("PASSWORD1234")]
    [InlineData("qwertyuiopas")]
    [InlineData("123456789012")]
    public void KnownPasswordsAreRejected_RegardlessOfCase(string password)
    {
        Assert.True(CommonPasswords.Contains(password));
        Assert.Contains("häufig benutzter", UserValidation.ValidatePassword(password));
    }

    [Fact]
    public void UncommonPasswordIsAccepted() =>
        Assert.Null(UserValidation.ValidatePassword("Kino-Abend mit 7 Freunden!"));

    [Fact]
    public void TooShortPasswordStillReportsTheLength() =>
        Assert.Contains("Zeichen lang", UserValidation.ValidatePassword("kurz"));
}
