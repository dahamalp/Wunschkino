using System.Net;
using System.Net.Sockets;
using System.Text;
using Wunschkino.Services;

namespace Wunschkino.Tests.Services;

public class OutboundAddressGuardTests
{
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("100.100.100.200")]
    [InlineData("fe80::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("::169.254.169.254")]
    [InlineData("::ffff:0:a9fe:a9fe")]
    [InlineData("0.0.0.0")]
    public void BlocksLinkLocalAndMetadataAddresses(string address) =>
        Assert.True(OutboundAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.2")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:c0a8:010a::1")]
    public void AllowsOtherAddresses(string address) =>
        Assert.False(OutboundAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public void Handler_IgnoresSystemProxies_SoTheTargetItselfIsChecked() =>
        Assert.False(OutboundAddressGuard.CreateHandler(allowLinkLocal: false).UseProxy);

    [Fact]
    public async Task Handler_RefusesMetadataAddressBeforeConnecting()
    {
        using var client = new HttpClient(OutboundAddressGuard.CreateHandler(allowLinkLocal: false));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://169.254.169.254/latest/meta-data/"));

        Assert.Contains("gesperrtes Netz", ex.ToString());
    }

    [Fact]
    public async Task Handler_ConnectsToAllowedAddresses()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync();
            await using var stream = connection.GetStream();
            await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
        });
        using var client = new HttpClient(OutboundAddressGuard.CreateHandler(allowLinkLocal: false));

        var response = await client.GetAsync($"http://127.0.0.1:{port}/");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await server;
    }
}
