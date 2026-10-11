namespace Wunschkino.Data;

/// <summary>
/// Fehlversuchs-Stand einer Client-Adresse. Die Zeile bleibt auch nach Ablauf einer Sperre bestehen, damit die nächste
/// Stufe länger dauert; eine erfolgreiche Anmeldung oder ein Admin löscht sie.
/// </summary>
public class IpBlock
{
    public int Id { get; set; }

    /// <summary>Schlüssel wie im Login-Limit: IPv4-Adresse oder bei IPv6 das ganze /64-Netz.</summary>
    public required string Address { get; set; }

    public int Failures { get; set; }
    public DateTime? BlockedUntilUtc { get; set; }

    /// <summary>Gilt, bis ein Admin entsperrt.</summary>
    public bool IsPermanent { get; set; }

    public DateTime LastFailureUtc { get; set; }
}
