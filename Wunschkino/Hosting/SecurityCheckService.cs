using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.DatabaseBackup;
using Wunschkino.Resources;

namespace Wunschkino.Hosting;

public enum CheckSeverity
{
    Ok,
    Info,
    Warning,
    Critical
}

/// <param name="Link">Seite in der App, auf der man das Problem ansieht oder behebt.</param>
public sealed record SecurityCheckItem(string Title, CheckSeverity Severity, string Message, string? Link = null)
{
    /// <summary>Feste Kennung für Prüfungen, auf die die Oberfläche reagiert; der Titel ist übersetzt und taugt dafür nicht.</summary>
    public string? Id { get; init; }

    public string SeverityLabel => Severity switch
    {
        CheckSeverity.Ok => Texts.Check_SeverityOk,
        CheckSeverity.Info => Texts.Check_SeverityInfo,
        CheckSeverity.Warning => Texts.Check_SeverityWarning,
        _ => Texts.Check_SeverityCritical
    };
}

/// <summary>
/// Prüft die Einstellungen und den Zustand der Installation und sagt Admins in einfachen Worten, was nicht stimmt. Der Betreiber soll
/// Probleme merken, ohne sich auszukennen. Als „kritisch“ gilt nur, was schon jetzt Schaden anrichtet oder Daten gefährdet.
/// </summary>
public sealed class SecurityCheckService(
    WunschkinoOptions options,
    TrustedProxies trustedProxies,
    BackupService backup,
    BackupSettings backupSettings,
    IpBlockService blocks,
    ProxyWarningState proxyWarning,
    TimeProvider time,
    IDbContextFactory<AppDbContext>? dbFactory = null)
{
    public const string ProxyId = "proxy";

    /// <summary>So lange gilt ein beobachteter, nicht eingetragener Proxy als aktuell.</summary>
    public static readonly TimeSpan ProxyWarningLifetime = TimeSpan.FromHours(24);

    /// <param name="includeBlocks">Ohne die Zählung der gesperrten Adressen (kostet eine Datenbankabfrage); das Banner braucht sie nicht.</param>
    public async Task<IReadOnlyList<SecurityCheckItem>> RunAsync(bool includeBlocks = true, CancellationToken ct = default)
    {
        var items = new List<SecurityCheckItem> { CheckProxy(), CheckDomain(), CheckCookies(), CheckRequestLimit(), CheckBackup(), KeyFolderHint() };
        if (includeBlocks)
        {
            items.Add(await CheckBlocksAsync(ct));
            if (dbFactory is not null)
                items.Add(await CheckAdminTwoFactorAsync(dbFactory, ct));
        }
        return items;
    }

    private SecurityCheckItem CheckProxy()
    {
        var title = Texts.Check_ProxyTitle;
        var now = time.GetUtcNow().UtcDateTime;
        if (proxyWarning.LastSeenSince(now - ProxyWarningLifetime) is { } seen)
            return new SecurityCheckItem(title, CheckSeverity.Critical, Format(Texts.Check_ProxyUntrusted, seen.Remote)) { Id = ProxyId };
        if (proxyWarning.LoopbackWithoutForwardingSince(now - ProxyWarningLifetime) is not null)
            return new SecurityCheckItem(title, CheckSeverity.Critical, Texts.Check_ProxyNoForwarding) { Id = ProxyId };

        if (trustedProxies.Addresses.Count + trustedProxies.Networks.Count > 0)
        {
            var listed = string.Join(", ", trustedProxies.Addresses.Select(a => a.ToString()).Concat(trustedProxies.Networks.Select(n => n.ToString())));
            return new SecurityCheckItem(title, CheckSeverity.Ok, Format(Texts.Check_ProxyListed, listed)) { Id = ProxyId };
        }

        return IsLoopbackBind(options.BindAddress)
            ? new SecurityCheckItem(title, CheckSeverity.Ok, Texts.Check_ProxyLocal) { Id = ProxyId }
            : new SecurityCheckItem(title, CheckSeverity.Warning, Texts.Check_ProxyMissing) { Id = ProxyId };
    }

    private SecurityCheckItem CheckDomain() =>
        string.IsNullOrWhiteSpace(options.Domain)
            ? new SecurityCheckItem(Texts.Check_DomainTitle, CheckSeverity.Warning, Texts.Check_DomainMissing)
            : new SecurityCheckItem(Texts.Check_DomainTitle, CheckSeverity.Ok, Format(Texts.Check_DomainAllowed, options.Domain));

    private SecurityCheckItem CheckCookies() =>
        options.SecureCookies
            ? new SecurityCheckItem(Texts.Check_CookiesTitle, CheckSeverity.Ok, Texts.Check_CookiesSecure)
            : new SecurityCheckItem(Texts.Check_CookiesTitle, CheckSeverity.Warning, Texts.Check_CookiesInsecure);

    private SecurityCheckItem CheckRequestLimit() =>
        options.RequestLimitPerMinute > 0
            ? new SecurityCheckItem(Texts.Check_LimitTitle, CheckSeverity.Ok, Format(Texts.Check_LimitOn, options.RequestLimitPerMinute))
            : new SecurityCheckItem(Texts.Check_LimitTitle, CheckSeverity.Warning, Texts.Check_LimitOff);

    private SecurityCheckItem CheckBackup()
    {
        var title = Texts.Check_BackupTitle;
        const string link = "/admin/security#sicherung";
        var status = backup.GetStatus();
        var now = time.GetUtcNow().UtcDateTime;

        if (status.LastError is not null)
            return new SecurityCheckItem(title, CheckSeverity.Critical, Format(Texts.Check_BackupFailed, status.LastError), link);
        if (status.LastBackupUtc is not { } last)
            return new SecurityCheckItem(title, CheckSeverity.Warning, Texts.Check_BackupNone, link);
        if (status.IsOverdue)
        {
            var days = (int)(now - last).TotalDays;
            return new SecurityCheckItem(title, CheckSeverity.Critical, Format(Texts.Check_BackupOverdue, days, backupSettings.WarnAfterDays), link);
        }
        if (status.DirectoryReadableByOthers)
            return new SecurityCheckItem(title, CheckSeverity.Warning, Format(Texts.Check_BackupReadable, status.Directory), link);
        if (status.SameMountAsDatabase)
            return new SecurityCheckItem(title, CheckSeverity.Warning, Texts.Check_BackupSameDrive, link);

        var message = status.Count == 1 ? Texts.Check_BackupOkOne : Texts.Check_BackupOkMany;
        return new SecurityCheckItem(title, CheckSeverity.Ok, Format(message, FormatLocal(last), status.Count), link);
    }

    private static SecurityCheckItem KeyFolderHint() => new(Texts.Check_KeysTitle, CheckSeverity.Info, Texts.Check_KeysHint);

    /// <summary>Nur eine Empfehlung: Die Zwei-Faktor-Anmeldung bleibt freiwillig, schützt Admin-Konten aber am meisten.</summary>
    private static async Task<SecurityCheckItem> CheckAdminTwoFactorAsync(IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct)
    {
        var title = Texts.Check_TwoFactorTitle;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var without = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Admin && !u.IsDisabled && u.TotpEnabledUtc == null)
            .OrderBy(u => u.Username)
            .Select(u => u.Username)
            .ToListAsync(ct);
        return without.Count == 0
            ? new SecurityCheckItem(title, CheckSeverity.Ok, Texts.Check_TwoFactorAll)
            : new SecurityCheckItem(title, CheckSeverity.Info, Format(Texts.Check_TwoFactorMissing, string.Join(", ", without)), "/account/2fa");
    }

    private async Task<SecurityCheckItem> CheckBlocksAsync(CancellationToken ct)
    {
        var title = Texts.Check_BlocksTitle;
        var now = time.GetUtcNow().UtcDateTime;
        var rows = await blocks.ListAsync(ct);
        var permanent = rows.Count(r => r.IsPermanent);
        var active = rows.Count(r => r.IsPermanent || r.BlockedUntilUtc > now);
        if (permanent > 0)
        {
            var message = permanent == 1 ? Texts.Check_BlocksPermanentOne : Texts.Check_BlocksPermanentMany;
            return new SecurityCheckItem(title, CheckSeverity.Warning, Format(message, permanent), "/admin/blocks");
        }

        if (active == 0)
            return new SecurityCheckItem(title, CheckSeverity.Ok, Texts.Check_BlocksNone, "/admin/blocks");
        return new SecurityCheckItem(title, CheckSeverity.Info, Format(active == 1 ? Texts.Check_BlocksActiveOne : Texts.Check_BlocksActiveMany, active), "/admin/blocks");
    }

    private static string Format(string format, params object?[] args) => string.Format(Texts.Culture, format, args);

    private static bool IsLoopbackBind(string? bind) =>
        bind is not null && (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase) || bind == "::1" || bind.StartsWith("127.", StringComparison.Ordinal));

    private static string FormatLocal(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
