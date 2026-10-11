namespace Wunschkino.Data;

/// <summary>
/// Ein Eintrag im Protokoll der sicherheitsrelevanten Aktionen (Anmeldung, Benutzerverwaltung, Verbindungseinstellungen, Sperren).
/// Der Benutzername steht mit im Eintrag, damit er auch nach dem Löschen eines Kontos lesbar bleibt.
/// </summary>
public class AuditEntry
{
    public int Id { get; set; }
    public DateTime TimeUtc { get; set; }

    /// <summary>Null bei Aktionen ohne angemeldeten Nutzer (Konsole, Automatik).</summary>
    public int? ActorUserId { get; set; }

    public string? ActorName { get; set; }

    /// <summary>Maschinenlesbarer Schlüssel, zum Beispiel „login“ oder „user.created“ (siehe <c>AuditActions</c>).</summary>
    public required string Action { get; set; }

    public string? Target { get; set; }
    public string? Detail { get; set; }

    /// <summary>Nur bei Anmeldungen; wird nach 90 Tagen gelöscht, der Eintrag selbst nach 365 Tagen. Bei gesperrten Adressen steht die Adresse im <see cref="Target"/> und fällt unter dieselbe Frist.</summary>
    public string? Ip { get; set; }
}
