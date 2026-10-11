namespace Wunschkino.Data;

/// <summary>
/// Ein Wiederherstellungscode für die Zwei-Faktor-Anmeldung. Gespeichert wird nur der Argon2-Hash (mit Salz, damit eine
/// gestohlene Datenbank die Codes nicht preisgibt); ein benutzter Code wird gelöscht.
/// </summary>
public class RecoveryCode
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string CodeHash { get; set; }
}
