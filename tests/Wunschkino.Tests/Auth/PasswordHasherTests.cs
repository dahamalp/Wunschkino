using Wunschkino.Auth;

namespace Wunschkino.Tests.Auth;

public class PasswordHasherTests
{
    private readonly Argon2IdPasswordHasher _hasher = new();

    [Fact]
    public void Verify_AcceptsCorrectPassword()
    {
        var hash = _hasher.Hash("korrektes-pferd-batterie");

        Assert.StartsWith("$argon2id$", hash);
        Assert.True(_hasher.Verify("korrektes-pferd-batterie", hash));
    }

    [Fact]
    public void Verify_RejectsWrongPassword()
    {
        var hash = _hasher.Hash("korrektes-pferd-batterie");

        Assert.False(_hasher.Verify("falsches-pferd-batterie", hash));
    }

    [Fact]
    public void Hash_UsesRandomSalt()
    {
        Assert.NotEqual(_hasher.Hash("gleiches-passwort"), _hasher.Hash("gleiches-passwort"));
    }
}
