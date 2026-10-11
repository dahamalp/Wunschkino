using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class ThemePreferenceServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ThemePreferenceService _service;
    private readonly User _user;

    public ThemePreferenceServiceTests()
    {
        _service = new ThemePreferenceService(_db);
        _user = TestUsers.Add(_db, "nutzer");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task NewAccounts_FollowTheSystem() =>
        Assert.Equal(ThemePreference.System, await _service.GetAsync(_user.Id));

    [Theory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.System)]
    public async Task Choice_IsStoredPerAccount(ThemePreference choice)
    {
        var other = TestUsers.Add(_db, "anderer");
        await _service.SetAsync(_user.Id, ThemePreference.Dark);

        await _service.SetAsync(_user.Id, choice);

        Assert.Equal(choice, await _service.GetAsync(_user.Id));
        Assert.Equal(ThemePreference.System, await _service.GetAsync(other.Id));
    }

    [Fact]
    public async Task ChangingTheChoice_KeepsExistingLoginsValid()
    {
        var stampBefore = TestUsers.Reload(_db, _user.Id).SecurityStamp;

        await _service.SetAsync(_user.Id, ThemePreference.Dark);

        Assert.Equal(stampBefore, TestUsers.Reload(_db, _user.Id).SecurityStamp);
    }

    [Fact]
    public async Task UnknownValues_AreIgnored()
    {
        await _service.SetAsync(_user.Id, (ThemePreference)42);

        Assert.Equal(ThemePreference.System, await _service.GetAsync(_user.Id));
    }

    [Fact]
    public async Task UnknownAccount_FallsBackToSystem() =>
        Assert.Equal(ThemePreference.System, await _service.GetAsync(9999));
}
