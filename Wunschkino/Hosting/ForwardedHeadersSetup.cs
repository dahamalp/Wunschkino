using Microsoft.AspNetCore.HttpOverrides;

namespace Wunschkino.Hosting;

public static class ForwardedHeadersSetup
{
    /// <summary>
    /// Übernimmt Client-IP und Protokoll nur von den angegebenen Proxys (Loopback ist immer vertraut). Das ist für das
    /// Login-Limit nötig: Ohne diese Zuordnung sähen alle Nutzer wie die IP des Proxys aus und teilten sich ein Limit.
    /// </summary>
    /// <remarks>
    /// Bewusst nicht <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>: Das leert die Listen und vertraut jedem, der die App
    /// erreicht – ein Angreifer könnte sich dann per <c>X-Forwarded-For</c> eine beliebige IP geben und das Limit umgehen.
    /// </remarks>
    public static ForwardedHeadersOptions Create(TrustedProxies trusted)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // Ein Proxy davor, also zählt nur der letzte Eintrag. Mehr würde Fälschungen durch den Client zulassen.
            ForwardLimit = 1
        };
        foreach (var address in trusted.Addresses)
            options.KnownProxies.Add(address);
        foreach (var network in trusted.Networks)
            options.KnownIPNetworks.Add(network);
        return options;
    }
}
