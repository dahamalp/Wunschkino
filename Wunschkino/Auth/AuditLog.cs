using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;
using Wunschkino.Resources;

namespace Wunschkino.Auth;

public static class AuditActions
{
    public const string Login = "login";
    public const string SetupCompleted = "setup.completed";
    public const string UserCreated = "user.created";
    public const string UserDisabled = "user.disabled";
    public const string UserEnabled = "user.enabled";
    public const string UserRole = "user.role";
    public const string UserPasswordReset = "user.password-reset";
    public const string UserUnlocked = "user.unlocked";
    public const string PasswordChanged = "account.password-changed";
    public const string TwoFactorEnabled = "account.2fa-enabled";
    public const string TwoFactorDisabled = "account.2fa-disabled";
    public const string RecoveryCodesRenewed = "account.2fa-recovery-renewed";
    public const string TwoFactorReset = "user.2fa-reset";
    public const string ConnectionsChanged = "settings.connections";
    public const string WishSettingsChanged = "settings.wishes";
    public const string LegalChanged = "settings.legal";
    public const string IpBlocked = "ip.blocked";
    public const string IpUnblocked = "ip.unblocked";
    public const string IpUnblockedAll = "ip.unblocked-all";

    public static string Label(string action) => action switch
    {
        Login => Texts.Audit_Login,
        SetupCompleted => Texts.Audit_SetupCompleted,
        UserCreated => Texts.Audit_UserCreated,
        UserDisabled => Texts.Audit_UserDisabled,
        UserEnabled => Texts.Audit_UserEnabled,
        UserRole => Texts.Audit_UserRole,
        UserPasswordReset => Texts.Audit_UserPasswordReset,
        UserUnlocked => Texts.Audit_UserUnlocked,
        PasswordChanged => Texts.Audit_PasswordChanged,
        TwoFactorEnabled => Texts.Audit_TwoFactorEnabled,
        TwoFactorDisabled => Texts.Audit_TwoFactorDisabled,
        RecoveryCodesRenewed => Texts.Audit_RecoveryCodesRenewed,
        TwoFactorReset => Texts.Audit_TwoFactorReset,
        ConnectionsChanged => Texts.Audit_ConnectionsChanged,
        WishSettingsChanged => Texts.Audit_WishSettingsChanged,
        LegalChanged => Texts.Audit_LegalChanged,
        IpBlocked => Texts.Audit_IpBlocked,
        IpUnblocked => Texts.Audit_IpUnblocked,
        IpUnblockedAll => Texts.Audit_IpUnblockedAll,
        _ => action
    };
}

/// <summary>
/// Protokoll der sicherheitsrelevanten Aktionen für Admins. Ein Fehler beim Schreiben darf die Aktion selbst nie scheitern lassen:
/// Er landet im Log, die Aktion läuft weiter. Aufbewahrung: Einträge 365 Tage, IP-Adressen (bei Anmeldungen und bei gesperrten
/// Adressen) 90 Tage.
/// </summary>
public sealed class AuditLog(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<AuditLog> logger)
{
    public const int MaxListed = 200;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(365);
    public static readonly TimeSpan IpRetention = TimeSpan.FromDays(90);

    private const int MaxActionLength = 48;
    private const int MaxNameLength = 32;
    private const int MaxTargetLength = 100;
    private const int MaxDetailLength = 300;
    private const int MaxIpLength = 64;

    private static readonly TimeSpan CleanUpInterval = TimeSpan.FromHours(1);

    private DateTime _lastCleanUpUtc;

    /// <returns>Falsch, wenn der Eintrag nicht gespeichert werden konnte (der Aufrufer bricht deshalb nicht ab).</returns>
    public async Task<bool> WriteAsync(
        string action,
        int? actorUserId,
        string? actorName,
        string? target = null,
        string? detail = null,
        string? ip = null,
        CancellationToken ct = default)
    {
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.AuditEntries.Add(new AuditEntry
            {
                TimeUtc = now,
                ActorUserId = actorUserId,
                ActorName = Clip(actorName, MaxNameLength),
                Action = Clip(action, MaxActionLength)!,
                Target = Clip(target, MaxTargetLength),
                Detail = Clip(detail, MaxDetailLength),
                Ip = Clip(ip, MaxIpLength)
            });
            await db.SaveChangesAsync(ct);
            await CleanUpIfDueAsync(db, now, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Der Eintrag „{Action}“ konnte nicht ins Protokoll geschrieben werden.", action);
            return false;
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(int max = MaxListed, bool includeLogins = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries.AsNoTracking()
            .Where(e => includeLogins || e.Action != AuditActions.Login)
            .OrderByDescending(e => e.TimeUtc).ThenByDescending(e => e.Id)
            .Take(Math.Clamp(max, 1, MaxListed))
            .ToListAsync(ct);
    }

    /// <summary>Löscht abgelaufene Einträge und kürzt die IP-Adressen. Läuft höchstens einmal pro Stunde beim Schreiben.</summary>
    private async Task CleanUpIfDueAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        if (now - _lastCleanUpUtc < CleanUpInterval)
            return;

        _lastCleanUpUtc = now;
        var oldest = now - Retention;
        var ipOldest = now - IpRetention;
        await db.AuditEntries.Where(e => e.TimeUtc < oldest).ExecuteDeleteAsync(ct);
        await db.AuditEntries.Where(e => e.Ip != null && e.TimeUtc < ipOldest)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Ip, (string?)null), ct);
        // Bei gesperrten Adressen steht die Adresse im Ziel statt in der IP-Spalte; sie fällt unter dieselbe Frist.
        await db.AuditEntries
            .Where(e => e.Target != null && e.TimeUtc < ipOldest
                && (e.Action == AuditActions.IpBlocked || e.Action == AuditActions.IpUnblocked))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Target, (string?)null), ct);
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
