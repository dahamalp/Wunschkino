using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino;
using Wunschkino.Auth;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class IpBlockServiceTests : IDisposable
{
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Server = IPAddress.Parse("192.168.1.5");

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly IpBlockService _blocks;

    public IpBlockServiceTests() => _blocks = Create();

    public void Dispose() => _db.Dispose();

    private IpBlockService Create(TrustedProxies? trusted = null) =>
        new(_db, _time, trusted ?? TrustedProxies.None, NullLogger<IpBlockService>.Instance);

    private async Task FailAsync(int times, IPAddress? address = null)
    {
        for (var i = 0; i < times; i++)
            await _blocks.RegisterFailureAsync(address ?? Client, Server);
    }

    private Task<IpBlockStatus> CheckAsync(IPAddress? address = null) => _blocks.CheckAsync(address ?? Client, Server);

    [Fact]
    public async Task TwoFailures_DoNotBlock()
    {
        await FailAsync(2);

        Assert.False((await CheckAsync()).IsBlocked);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(6, 5)]
    [InlineData(9, 15)]
    [InlineData(12, 60)]
    [InlineData(15, 24 * 60)]
    public async Task EveryThirdFailure_BlocksLongerThanBefore(int failures, int expectedMinutes)
    {
        for (var i = 1; i <= failures; i++)
        {
            await FailAsync(1);
            // Eine laufende Sperre zählt keine weiteren Versuche: Vor dem nächsten Fehlversuch läuft sie ab.
            if (i % 3 == 0 && i < failures)
                _time.Advance(TimeSpan.FromHours(25));
        }

        var status = await CheckAsync();

        Assert.True(status.IsBlocked);
        Assert.False(status.IsPermanent);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddMinutes(expectedMinutes), status.UntilUtc);
    }

    [Fact]
    public async Task AfterTheLastTimedStage_TheAddressIsBlockedPermanentlyUntilAnAdminUnblocks()
    {
        for (var i = 1; i <= 18; i++)
        {
            await FailAsync(1);
            if (i % 3 == 0 && i < 18)
                _time.Advance(TimeSpan.FromHours(25));
        }
        _time.Advance(TimeSpan.FromDays(10));

        var status = await CheckAsync();

        Assert.True(status.IsBlocked);
        Assert.True(status.IsPermanent);
        Assert.Null(status.UntilUtc);

        Assert.True(await _blocks.UnblockAsync(Client.ToString()));
        Assert.False((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task ABlock_EndsWhenItsTimeIsUp()
    {
        await FailAsync(3);
        Assert.True((await CheckAsync()).IsBlocked);

        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.False((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task AttemptsDuringABlock_DoNotCountTowardsTheNextStage()
    {
        await FailAsync(3);
        await FailAsync(10);
        _time.Advance(TimeSpan.FromSeconds(61));

        await FailAsync(3);

        var status = await CheckAsync();
        Assert.True(status.IsBlocked);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddMinutes(5), status.UntilUtc);
    }

    [Fact]
    public async Task AfterABlock_TheNextThreeFailuresStartTheNextStage_NotJustOne()
    {
        await FailAsync(3);
        _time.Advance(TimeSpan.FromSeconds(61));

        await FailAsync(2);

        Assert.False((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task ASuccessfulLogin_LowersTheCounterByOneOnly()
    {
        await FailAsync(2);
        await _blocks.RegisterSuccessAsync(Client, Server);

        await FailAsync(1);
        Assert.False((await CheckAsync()).IsBlocked);
        await FailAsync(1);

        Assert.True((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task AttackerWithAnOwnAccount_CannotResetTheCounterByLoggingInBetweenGuesses()
    {
        for (var round = 0; round < 6; round++)
        {
            await FailAsync(2);
            await _blocks.RegisterSuccessAsync(Client, Server);
        }

        Assert.True((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task EnoughSuccessfulLogins_BringTheCounterBackToZero_AndRemoveTheRow()
    {
        await FailAsync(2);
        await _blocks.RegisterSuccessAsync(Client, Server);
        await _blocks.RegisterSuccessAsync(Client, Server);

        Assert.Empty(await _blocks.ListAsync());
    }

    [Fact]
    public async Task ASuccessfulLogin_DoesNotEndARunningBlock()
    {
        await FailAsync(3);

        await _blocks.RegisterSuccessAsync(Client, Server);

        Assert.True((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task OldCountersOfNonPermanentAddresses_AreCleanedUpAfterTheDecayTime()
    {
        await FailAsync(2);
        _time.Advance(IpBlockService.Decay + TimeSpan.FromDays(1));

        await CheckAsync();

        Assert.Empty(await _blocks.ListAsync());
    }

    [Fact]
    public async Task PermanentBlocks_AreNeverCleanedUp_AndListedFirst()
    {
        for (var i = 1; i <= 18; i++)
        {
            await FailAsync(1);
            if (i % 3 == 0 && i < 18)
                _time.Advance(TimeSpan.FromHours(25));
        }
        await FailAsync(2, IPAddress.Parse("203.0.113.8"));
        _time.Advance(IpBlockService.Decay + TimeSpan.FromDays(1));
        await CheckAsync();
        await FailAsync(1, IPAddress.Parse("203.0.113.9"));

        var listed = await _blocks.ListAsync();

        Assert.Equal("203.0.113.7", listed[0].Address);
        Assert.True(listed[0].IsPermanent);
        Assert.DoesNotContain(listed, b => b.Address == "203.0.113.8");
    }

    [Fact]
    public async Task ThirtyDaysWithoutFailures_StartOverFromTheFirstStage()
    {
        await FailAsync(3);
        _time.Advance(IpBlockService.Decay + TimeSpan.FromDays(1));

        await FailAsync(3);

        var status = await CheckAsync();
        Assert.True(status.IsBlocked);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddMinutes(1), status.UntilUtc);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("192.168.1.5")]
    public async Task TheServerItself_IsNeverBlocked(string address)
    {
        await FailAsync(30, IPAddress.Parse(address));

        Assert.False((await CheckAsync(IPAddress.Parse(address))).IsBlocked);
        Assert.Empty(await _blocks.ListAsync());
    }

    [Fact]
    public async Task ATrustedProxy_IsNeverBlocked_ButOtherAddressesNextToItAre()
    {
        var blocks = Create(new TrustedProxies([IPAddress.Parse("10.0.0.5")], [IPNetwork.Parse("172.16.0.0/24")]));

        foreach (var proxy in new[] { "10.0.0.5", "172.16.0.9" })
            for (var i = 0; i < 12; i++)
                await blocks.RegisterFailureAsync(IPAddress.Parse(proxy), Server);
        for (var i = 0; i < 3; i++)
            await blocks.RegisterFailureAsync(IPAddress.Parse("10.0.0.6"), Server);

        Assert.False((await blocks.CheckAsync(IPAddress.Parse("10.0.0.5"), Server)).IsBlocked);
        Assert.False((await blocks.CheckAsync(IPAddress.Parse("172.16.0.9"), Server)).IsBlocked);
        Assert.True((await blocks.CheckAsync(IPAddress.Parse("10.0.0.6"), Server)).IsBlocked);
    }

    [Fact]
    public async Task OtherAddresses_AreNotAffected()
    {
        await FailAsync(3);

        Assert.False((await CheckAsync(IPAddress.Parse("203.0.113.8"))).IsBlocked);
    }

    [Fact]
    public async Task AnIpv6Prefix_CountsAsOneAddress()
    {
        for (var i = 1; i <= 3; i++)
            await _blocks.RegisterFailureAsync(IPAddress.Parse($"2001:db8:1:2::{i:x}"), Server);

        Assert.True((await CheckAsync(IPAddress.Parse("2001:db8:1:2:ffff::1"))).IsBlocked);
        Assert.False((await CheckAsync(IPAddress.Parse("2001:db8:1:3::1"))).IsBlocked);
    }

    [Fact]
    public async Task ABlock_SurvivesARestart()
    {
        await FailAsync(3);

        var afterRestart = Create();

        Assert.True((await afterRestart.CheckAsync(Client, Server)).IsBlocked);
    }

    [Fact]
    public async Task AnUnblockFromAnotherProcess_ReachesTheRunningServiceWithinTheCacheLifetime()
    {
        await FailAsync(3);
        Assert.True((await CheckAsync()).IsBlocked);

        await Create().UnblockAsync(Client.ToString());
        Assert.True((await CheckAsync()).IsBlocked);

        _time.Advance(TimeSpan.FromSeconds(11));

        Assert.False((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task UnblockAll_RemovesEveryAddress()
    {
        await FailAsync(3);
        await FailAsync(3, IPAddress.Parse("203.0.113.8"));

        var removed = await _blocks.UnblockAllAsync();

        Assert.Equal(2, removed);
        Assert.Empty(await _blocks.ListAsync());
        Assert.False((await CheckAsync()).IsBlocked);
    }

    [Fact]
    public async Task Unblock_OfAnUnknownAddress_ReportsFalse()
    {
        Assert.False(await _blocks.UnblockAsync("198.51.100.1"));
    }

    [Fact]
    public async Task ParallelFailures_AreCountedExactly()
    {
        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => _blocks.RegisterFailureAsync(Client, Server)));

        var block = Assert.Single(await _blocks.ListAsync());
        Assert.Equal(3, block.Failures);
        Assert.True((await CheckAsync()).IsBlocked);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData(" 203.0.113.7 ", "203.0.113.7")]
    [InlineData("2001:db8:1:2::5", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::/64", "2001:db8:1:2::/64")]
    public void NormalizeKey_MatchesTheKeyOfTheLoginLimit(string input, string expected)
    {
        Assert.Equal(expected, IpBlockService.NormalizeKey(input));
    }
}
