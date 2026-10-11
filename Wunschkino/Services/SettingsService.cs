using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Services;

public static class SettingKeys
{
    public const string JellyfinUrl = "Jellyfin.Url";
    public const string JellyfinApiKey = "Jellyfin.ApiKey";
    public const string TmdbApiKey = "Tmdb.ApiKey";
    public const string JellyfinRefreshMinutes = "Jellyfin.RefreshMinutes";

    /// <summary>„true“ oder „false“; fehlt der Wert, gilt anonym (die datenschutzfreundliche Voreinstellung).</summary>
    public const string WishesAnonymous = "Wishes.Anonymous";

    /// <summary>Wünsche (neue und „+1“) je Nutzer in den letzten 24 Stunden; 0 = kein Limit, Admins sind ausgenommen.</summary>
    public const string WishesPerDay = "Limits.WishesPerDay";

    /// <summary>TMDB-Suchen je Nutzer und Minute; 0 = kein Limit.</summary>
    public const string TmdbSearchesPerMinute = "Limits.TmdbSearchesPerMinute";

    public const string LegalImprint = "Legal.Imprint";
    public const string DefaultLanguage = "Ui.DefaultLanguage";
    public const string LegalPrivacy = "Legal.Privacy";
    public const string LegalContact = "Legal.Contact";

    /// <summary>Geheimer Schlüssel (Data Protection verschlüsselt), mit dem die Wiederherstellungscodes der Zwei-Faktor-Anmeldung gehasht werden.</summary>
    public const string RecoveryCodeKey = "TwoFactor.RecoveryKey";
}

public static class WishPrivacy
{
    public static bool IsAnonymous(string? storedValue) => !string.Equals(storedValue, "false", StringComparison.OrdinalIgnoreCase);
}

public sealed class SettingsService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    ILogger<SettingsService> logger)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Wunschkino.Settings.v1");

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting?.Value is null)
            return null;
        if (!setting.IsProtected)
            return setting.Value;

        try
        {
            return _protector.Unprotect(setting.Value);
        }
        catch (CryptographicException)
        {
            // Passiert, wenn der Schlüsselordner verloren ging oder ersetzt wurde. Der Wert muss dann neu eingegeben werden.
            logger.LogWarning("Die Einstellung {Key} lässt sich nicht entschlüsseln und muss neu eingetragen werden.", key);
            return null;
        }
    }

    public async Task SetAsync(string key, string? value, bool isProtected, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await StageAsync(db, key, value, isProtected, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Merkt die Änderung nur im übergebenen Context vor, damit Aufrufer sie in ihrer eigenen Transaktion speichern.</summary>
    public async Task StageAsync(AppDbContext db, string key, string? value, bool isProtected, CancellationToken ct = default)
    {
        var stored = value is not null && isProtected ? _protector.Protect(value) : value;
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = stored, IsProtected = isProtected });
            return;
        }
        setting.Value = stored;
        setting.IsProtected = isProtected;
    }
}
