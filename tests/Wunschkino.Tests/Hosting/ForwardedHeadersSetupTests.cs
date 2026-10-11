using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wunschkino.Hosting;

namespace Wunschkino.Tests.Hosting;

public class ForwardedHeadersSetupTests
{
    /// <summary>Lässt eine Anfrage durch die echte Middleware laufen und liefert, was die Anwendung danach sieht.</summary>
    private static async Task<(IPAddress? ClientIp, string Scheme)> RunAsync(
        string? trustedProxies, string remoteIp, string forwardedFor, string forwardedProto = "https")
    {
        var trusted = new WunschkinoOptions { TrustedProxies = trustedProxies }.ResolveTrustedProxies();
        var options = ForwardedHeadersSetup.Create(trusted);
        (IPAddress? Ip, string Scheme) seen = default;
        var middleware = new ForwardedHeadersMiddleware(
            context =>
            {
                seen = (context.Connection.RemoteIpAddress, context.Request.Scheme);
                return Task.CompletedTask;
            },
            NullLoggerFactory.Instance,
            Options.Create(options));

        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        http.Request.Scheme = "http";
        http.Request.Headers["X-Forwarded-For"] = forwardedFor;
        http.Request.Headers["X-Forwarded-Proto"] = forwardedProto;

        await middleware.Invoke(http);
        return seen;
    }

    [Fact]
    public async Task UntrustedSender_CannotForgeTheClientIpOrScheme()
    {
        var (ip, scheme) = await RunAsync(trustedProxies: "10.0.0.5", remoteIp: "203.0.113.9", forwardedFor: "1.2.3.4");

        Assert.Equal(IPAddress.Parse("203.0.113.9"), ip);
        Assert.Equal("http", scheme);
    }

    [Fact]
    public async Task WithoutAnyConfiguredProxy_OnlyLoopbackIsTrusted()
    {
        var external = await RunAsync(null, "10.0.0.5", "1.2.3.4");
        var loopback = await RunAsync(null, "127.0.0.1", "1.2.3.4");

        Assert.Equal(IPAddress.Parse("10.0.0.5"), external.ClientIp);
        Assert.Equal(IPAddress.Parse("1.2.3.4"), loopback.ClientIp);
    }

    [Fact]
    public async Task TrustedProxyAddress_ForwardsClientIpAndScheme()
    {
        var (ip, scheme) = await RunAsync("10.0.0.5", "10.0.0.5", "198.51.100.7");

        Assert.Equal(IPAddress.Parse("198.51.100.7"), ip);
        Assert.Equal("https", scheme);
    }

    [Fact]
    public async Task TrustedNetwork_ForwardsForAnyAddressInIt()
    {
        var (ip, _) = await RunAsync("192.168.1.0/24, 10.9.9.9", "192.168.1.77", "198.51.100.7");

        Assert.Equal(IPAddress.Parse("198.51.100.7"), ip);
    }

    [Fact]
    public async Task TrustedProxy_IsRecognisedEvenWhenTheListenerReportsItAsIpv4MappedIpv6()
    {
        // Ein Dual-Mode-Socket (http://*:port) meldet IPv4-Absender in dieser Form.
        var (ip, _) = await RunAsync("10.0.0.5", "::ffff:10.0.0.5", "198.51.100.7");

        Assert.Equal(IPAddress.Parse("198.51.100.7"), ip);
    }

    [Fact]
    public async Task OnlyTheLastHopCounts_SoAClientCannotPrependAFakeAddress()
    {
        // Der Client hat „6.6.6.6“ vorangestellt, der vertraute Proxy hängt die echte Adresse hinten an.
        var (ip, _) = await RunAsync("10.0.0.5", "10.0.0.5", "6.6.6.6, 198.51.100.7");

        Assert.Equal(IPAddress.Parse("198.51.100.7"), ip);
    }

    [Fact]
    public void Create_OnlyProcessesForAndProtoHeaders()
    {
        var options = ForwardedHeadersSetup.Create(TrustedProxies.None);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
    }
}
