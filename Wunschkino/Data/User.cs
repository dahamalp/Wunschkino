namespace Wunschkino.Data;

public enum UserRole
{
    User = 0,
    Admin = 1
}

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}

public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; }
    public bool IsDisabled { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gewählte Darstellung; gilt auf allen Geräten des Kontos und ändert nie den SecurityStamp.</summary>
    public ThemePreference ThemePreference { get; set; }

    /// <summary>Gewählte Sprache der Oberfläche (zum Beispiel „en“); null heißt: Sprache des Browsers.</summary>
    public string? Language { get; set; }

    /// <summary>Geheimer TOTP-Schlüssel, mit Data Protection verschlüsselt; null, solange die Zwei-Faktor-Anmeldung aus ist.</summary>
    public string? TotpSecret { get; set; }

    public DateTime? TotpEnabledUtc { get; set; }

    /// <summary>Zuletzt angenommenes TOTP-Zeitfenster; ältere und gleiche Fenster gelten nicht mehr (kein Code zweimal).</summary>
    public long TotpLastTimeStep { get; set; }

    public bool HasTwoFactor => TotpEnabledUtc is not null;

    /// <summary>Falsche Codes im zweiten Schritt der Anmeldung seit dem letzten Erfolg; in der DB, damit ein Neustart die Sperre nicht aufhebt.</summary>
    public int SecondFactorFailures { get; set; }

    public DateTime? SecondFactorLastFailureUtc { get; set; }

    public DateTime? SecondFactorLockedUntilUtc { get; set; }

    /// <summary>Gesetzt, wenn ein Admin das Passwort vergeben hat: Der Nutzer muss nach der Anmeldung ein eigenes wählen.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Ändert sich bei Passwort-, Rollen- oder Sperränderungen und macht damit bestehende Login-Cookies ungültig.</summary>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
}
