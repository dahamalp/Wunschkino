using System.Net;

namespace Wunschkino.Hosting;

/// <summary>
/// Merkt sich im Arbeitsspeicher, dass ein Absender <c>X-Forwarded-For</c> geschickt hat, ohne als Proxy eingetragen zu sein. Die
/// Sicherheitscheck-Seite zeigt das als kritisch: Alle Nutzer hinter dieser Adresse teilen sich Limit und Sperre.
/// </summary>
public sealed class ProxyWarningState
{
    private readonly Lock _gate = new();
    private (IPAddress? Remote, DateTime SeenUtc)? _lastSeen;

    public void Record(IPAddress? remote, DateTime nowUtc)
    {
        lock (_gate)
            _lastSeen = (remote, nowUtc);
    }

    /// <summary>Verwirft den Hinweis, etwa nachdem der Admin den Proxy eingetragen hat oder den Absender für harmlos hält.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lastSeen = null;
            _lastLoopbackWithoutForwarding = null;
        }
    }

    private DateTime? _lastLoopbackWithoutForwarding;

    /// <summary>Eine Anfrage kam von der Loopback-Adresse, trug aber einen fremden Host-Namen und kein <c>X-Forwarded-For</c>.</summary>
    public void RecordLoopbackWithoutForwarding(DateTime nowUtc)
    {
        lock (_gate)
            _lastLoopbackWithoutForwarding = nowUtc;
    }

    public DateTime? LoopbackWithoutForwardingSince(DateTime since)
    {
        lock (_gate)
            return _lastLoopbackWithoutForwarding is { } seen && seen >= since ? seen : null;
    }

    /// <summary>Der zuletzt aufgefallene Absender, nur wenn das nach <paramref name="since"/> geschah.</summary>
    public (IPAddress? Remote, DateTime SeenUtc)? LastSeenSince(DateTime since)
    {
        lock (_gate)
            return _lastSeen is { } seen && seen.SeenUtc >= since ? seen : null;
    }
}
