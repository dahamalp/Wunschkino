using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino;
using Wunschkino.Auth;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class LoginGuardTests : IDisposable
{
    private const string Password = "ein-sicheres-passwort";
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Server = IPAddress.Parse("192.168.1.5");

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger<LoginGuard> _log = new();
    private readonly IpBlockService _blocks;
    private readonly LoginGuard _guard;

    public LoginGuardTests()
    {
        _blocks = new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance);
        _guard = new LoginGuard(new LoginService(_db, new PlainTextHasher(), _time), _blocks, _time, _log);
        TestUsers.Add(_db, "anna");
    }

    public void Dispose() => _db.Dispose();

    private Task<LoginResult> AttemptAsync(string username, string password, IPAddress? remote = null) =>
        _guard.AttemptAsync(username, password, remote ?? Client, Server);

    [Fact]
    public async Task ACorrectLogin_Succeeds_AndWritesNoFailureLine()
    {
        var result = await AttemptAsync("anna", Password);

        Assert.True(result.Success);
        Assert.Empty(_log.Messages);
    }

    [Fact]
    public async Task AWrongPassword_WritesOneFixedLogLine_ForExternalTools()
    {
        await AttemptAsync("anna", "falsch");

        Assert.Equal("LoginFailed ip=203.0.113.7 user=\"anna\"", Assert.Single(_log.Messages));
    }

    [Fact]
    public async Task TheLoggedUsername_CannotInjectFakeLines()
    {
        await AttemptAsync("anna\"\nLoginFailed ip=198.51.100.1 user=\"admin", "falsch");

        var line = Assert.Single(_log.Messages);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal(1, line.Count(c => c == '"') / 2);
        Assert.StartsWith("LoginFailed ip=203.0.113.7 user=\"anna", line);
    }

    [Fact]
    public async Task AnIpv4MappedAddress_IsLoggedAsPlainIpv4()
    {
        await AttemptAsync("anna", "falsch", IPAddress.Parse("::ffff:203.0.113.7"));

        Assert.Equal("LoginFailed ip=203.0.113.7 user=\"anna\"", Assert.Single(_log.Messages));
    }

    [Fact]
    public async Task TheThirdWrongPassword_BlocksTheAddress_AndSaysForHowLong()
    {
        await AttemptAsync("anna", "falsch");
        await AttemptAsync("anna", "falsch");

        var third = await AttemptAsync("anna", "falsch");

        Assert.False(third.Success);
        Assert.Contains("gesperrt", third.Error);
        Assert.Contains("in 1 Minute", third.Error);
        Assert.True((await _blocks.CheckAsync(Client, Server)).IsBlocked);
    }

    [Fact]
    public async Task TheFirstWrongPasswords_ShowTheGenericMessage_WithoutRevealingAnything()
    {
        var result = await AttemptAsync("anna", "falsch");

        Assert.Equal(LoginService.GenericError, result.Error);
    }

    [Fact]
    public async Task ASuccessfulLogin_LowersTheFailuresOfTheAddress_ButDoesNotForgetThem()
    {
        await AttemptAsync("anna", "falsch");
        await AttemptAsync("anna", "falsch");
        await AttemptAsync("anna", Password);

        await AttemptAsync("anna", "falsch");
        Assert.False((await _blocks.CheckAsync(Client, Server)).IsBlocked);
        await AttemptAsync("anna", "falsch");

        Assert.True((await _blocks.CheckAsync(Client, Server)).IsBlocked);
    }

    [Fact]
    public async Task ABlockThatStoodInTheMeantime_StopsTheAttemptBeforeThePasswordIsChecked()
    {
        for (var i = 0; i < 3; i++)
            await _blocks.RegisterFailureAsync(Client, Server);

        var result = await AttemptAsync("anna", Password);

        Assert.False(result.Success);
        Assert.Contains("gesperrt", result.Error);
    }

    [Fact]
    public async Task TheServerItself_IsNotBlocked_EvenAfterManyFailures()
    {
        // Wechselnde Namen, damit die Bremse pro Benutzername (mit angehaltener Testuhr) nicht greift.
        for (var i = 0; i < 10; i++)
            await _guard.AttemptAsync($"fremd{i}", "falsch", IPAddress.Loopback, Server);

        var result = await _guard.AttemptAsync("anna", Password, IPAddress.Loopback, Server);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task AfterFiveFailuresOfOneUsername_EachFurtherAttemptWaitsAFewSeconds_EvenFromOtherAddresses()
    {
        for (var i = 0; i < LoginGuard.FailuresBeforeSlowdown; i++)
            await AttemptAsync("anna", "falsch", IPAddress.Parse($"198.51.100.{i + 1}"));

        var next = AttemptAsync("anna", Password, IPAddress.Parse("198.51.100.200"));
        await Task.Delay(50);
        Assert.False(next.IsCompleted, "Der Versuch muss warten.");

        _time.Advance(LoginGuard.Slowdown);
        Assert.True((await next).Success);
    }

    [Fact]
    public async Task TheSlowdown_NeverLocksTheAccount_AndSparesOtherUsernames()
    {
        TestUsers.Add(_db, "berta");
        for (var i = 0; i < LoginGuard.FailuresBeforeSlowdown; i++)
            await AttemptAsync("anna", "falsch", IPAddress.Parse($"198.51.100.{i + 1}"));

        var other = await AttemptAsync("berta", Password, IPAddress.Parse("198.51.100.150"));

        Assert.True(other.Success);
    }

    [Fact]
    public async Task TheSlowdown_AlsoAppliesToNamesThatDoNotExist_SoItRevealsNothing()
    {
        for (var i = 0; i < LoginGuard.FailuresBeforeSlowdown; i++)
            await AttemptAsync("gibtsnicht", "falsch", IPAddress.Parse($"198.51.100.{i + 1}"));

        var next = AttemptAsync("gibtsnicht", "falsch", IPAddress.Parse("198.51.100.200"));
        await Task.Delay(50);

        Assert.False(next.IsCompleted);
        _time.Advance(LoginGuard.Slowdown);
        await next;
    }

    [Fact]
    public async Task TheSlowdown_EndsAfterTheWindow()
    {
        for (var i = 0; i < LoginGuard.FailuresBeforeSlowdown; i++)
            await AttemptAsync("anna", "falsch", IPAddress.Parse($"198.51.100.{i + 1}"));
        _time.Advance(LoginGuard.UserFailureWindow + TimeSpan.FromSeconds(1));

        var next = AttemptAsync("anna", Password, IPAddress.Parse("198.51.100.200"));

        Assert.Same(next, await Task.WhenAny(next, Task.Delay(2000)));
        Assert.True((await next).Success);
    }

    [Theory]
    [InlineData(1, "in 1 Minute")]
    [InlineData(61, "in 2 Minuten")]
    [InlineData(300, "in 5 Minuten")]
    [InlineData(3540, "in 59 Minuten")]
    [InlineData(3599, "in 1 Stunde")]
    [InlineData(3600, "in 1 Stunde")]
    [InlineData(86400, "in 24 Stunden")]
    public void FormatWait_RoundsUp_ToAReadableUnit(int seconds, string expected)
    {
        Assert.Equal(expected, LoginGuard.FormatWait(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ThePermanentMessage_PointsToAnAdmin()
    {
        Assert.Contains("Admin", _guard.BlockedMessage(new IpBlockStatus(true, null, true)));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
