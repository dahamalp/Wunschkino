using Wunschkino.Setup;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Setup;

public class SetupTokenStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly SetupTokenStore _store;

    public SetupTokenStoreTests() => _store = new SetupTokenStore(_dir.Path);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void EnsureExists_WritesRandomHexToken()
    {
        _store.EnsureExists();

        Assert.Matches("^[0-9a-f]{32}$", File.ReadAllText(_store.TokenPath));
    }

    [Fact]
    public void EnsureExists_KeepsExistingToken()
    {
        _store.EnsureExists();
        var first = File.ReadAllText(_store.TokenPath);

        _store.EnsureExists();

        Assert.Equal(first, File.ReadAllText(_store.TokenPath));
    }

    [Fact]
    public void IsValid_AcceptsTokenIgnoringCaseAndWhitespace()
    {
        _store.EnsureExists();
        var token = File.ReadAllText(_store.TokenPath);

        Assert.True(_store.IsValid($"  {token.ToUpperInvariant()} "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    public void IsValid_RejectsWrongToken(string? candidate)
    {
        _store.EnsureExists();

        Assert.False(_store.IsValid(candidate));
    }

    [Fact]
    public void IsValid_RejectsEverythingAfterDelete()
    {
        _store.EnsureExists();
        var token = File.ReadAllText(_store.TokenPath);

        _store.Delete();

        Assert.False(_store.IsValid(token));
    }
}
