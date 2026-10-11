using System.Net;
using System.Net.Sockets;

namespace Wunschkino.Services;

/// <summary>
/// Verhindert, dass ausgehende Verbindungen Link-Local- oder Cloud-Metadaten-Adressen erreichen (Schutz vor SSRF über eine
/// vom Admin eingetragene Jellyfin-Adresse). Geprüft wird erst nach der DNS-Auflösung beim Verbindungsaufbau, damit auch ein
/// Hostname, der auf eine solche Adresse zeigt, nicht durchkommt. Private Netze (192.168.x.x usw.) bleiben erlaubt, ebenso
/// 100.64.0.0/10, weil dort etwa Tailscale-Adressen liegen, unter denen Jellyfin oft erreichbar ist. Ein System-Proxy wird
/// nicht benutzt: Sonst sähe die Prüfung nur die Adresse des Proxys, nie das eigentliche Ziel.
/// </summary>
public static class OutboundAddressGuard
{
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("100.100.100.200/32"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fd00:ec2::254/128")
    ];

    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (EmbeddedIPv4(address) is { } embedded && BlockedNetworks.Any(network => network.Contains(embedded)))
            return true;
        return BlockedNetworks.Any(network => network.Contains(address));
    }

    /// <summary>IPv4-Adressen in IPv6-Formen (NAT64, 6to4, „IPv4-kompatibel“, „IPv4-übersetzt“), die ins IPv4-Netz weiterleiten.</summary>
    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return null;
        var bytes = address.GetAddressBytes();
        if (Nat64.Contains(address))
            return new IPAddress(bytes[12..16]);
        if (SixToFour.Contains(address))
            return new IPAddress(bytes[2..6]);
        var upperZero = bytes[..8].All(b => b == 0);
        if (upperZero && bytes[8..12].All(b => b == 0) && !IPAddress.IPv6Loopback.Equals(address) && !IPAddress.IPv6Any.Equals(address))
            return new IPAddress(bytes[12..16]);
        if (upperZero && bytes[8] == 0xff && bytes[9] == 0xff && bytes[10] == 0 && bytes[11] == 0)
            return new IPAddress(bytes[12..16]);
        return null;
    }

    public static SocketsHttpHandler CreateHandler(bool allowLinkLocal)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (!allowLinkLocal)
        {
            handler.UseProxy = false;
            handler.ConnectCallback = ConnectAsync;
        }
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"Die Adresse {host} zeigt auf ein gesperrtes Netz (Link-Local oder Cloud-Metadaten).");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
