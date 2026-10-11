using System.Net;
using Wunschkino.Hosting;

namespace Wunschkino.Tests.Hosting;

public class ClientAddressTests
{
    private static string Key(string? address) => ClientAddress.PartitionKey(address is null ? null : IPAddress.Parse(address));

    [Fact]
    public void PartitionKey_KeepsIpv4Addresses_AndHandlesMissingOnes()
    {
        Assert.Equal("203.0.113.9", Key("203.0.113.9"));
        Assert.Equal("unbekannt", Key(null));
    }

    [Fact]
    public void PartitionKey_TreatsIpv4MappedAddressesLikeIpv4()
    {
        Assert.Equal("203.0.113.9", Key("::ffff:203.0.113.9"));
    }

    [Fact]
    public void PartitionKey_GroupsAllAddressesOfAnIpv6Slash64()
    {
        var first = Key("2001:db8:1:2::1");
        var other = Key("2001:db8:1:2:ffff:ffff:ffff:ffff");

        Assert.Equal(first, other);
        Assert.Equal("2001:db8:1:2::/64", first);
    }

    [Fact]
    public void PartitionKey_SeparatesDifferentIpv6Networks()
    {
        Assert.NotEqual(Key("2001:db8:1:2::1"), Key("2001:db8:1:3::1"));
    }

    [Theory]
    [InlineData("127.0.0.1", null, true)]
    [InlineData("::1", null, true)]
    [InlineData("::ffff:127.0.0.1", null, true)]
    [InlineData("192.168.1.5", "192.168.1.5", true)]
    [InlineData("::ffff:192.168.1.5", "192.168.1.5", true)]
    [InlineData("192.168.1.5", "::ffff:192.168.1.5", true)]
    [InlineData("192.168.1.9", "192.168.1.5", false)]
    [InlineData("203.0.113.9", null, false)]
    [InlineData(null, "192.168.1.5", false)]
    public void IsLocal_RecognisesCallsFromTheServerItself(string? remote, string? local, bool expected)
    {
        var result = ClientAddress.IsLocal(
            remote is null ? null : IPAddress.Parse(remote),
            local is null ? null : IPAddress.Parse(local));

        Assert.Equal(expected, result);
    }
}
