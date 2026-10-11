using System.Net;
using System.Net.Sockets;

namespace Wunschkino.Hosting;

public static class ClientAddress
{
    /// <summary>
    /// Schlüssel, unter dem das Login-Limit einen Client zählt. IPv6-Adressen zählen als ganzes /64-Netz: Ein Anschluss
    /// bekommt üblicherweise so ein Netz und könnte sonst mit jedem Versuch eine neue Quelladresse nehmen.
    /// </summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
            return "unbekannt";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>Wahr, wenn die Verbindung vom Server selbst kommt (Loopback oder die eigene Adresse), etwa vom Health-Check des Install-Scripts.</summary>
    public static bool IsLocal(IPAddress? remote, IPAddress? local)
    {
        if (remote is null)
            return false;
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote))
            return true;
        if (local is null)
            return false;
        if (local.IsIPv4MappedToIPv6)
            local = local.MapToIPv4();
        return remote.Equals(local);
    }
}
