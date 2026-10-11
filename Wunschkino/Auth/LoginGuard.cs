using System.Net;
using System.Text;
using Wunschkino.Resources;

namespace Wunschkino.Auth;

/// <summary>
/// Schützt die Anmeldung vor Raten: Jeder Fehlversuch zählt für die Absender-Adresse (gestaffelte Sperre in
/// <see cref="IpBlockService"/>) und steht in einer festen Log-Zeile, die Werkzeuge wie Fail2ban auswerten können.
/// Zusätzlich bremst eine weiche Verzögerung pro Benutzername verteiltes Raten, ohne dass Anmeldeversuche ein Konto sperren.
/// </summary>
public sealed class LoginGuard(
    LoginService login,
    IpBlockService blocks,
    TimeProvider time,
    ILogger<LoginGuard> logger,
    AuditLog? audit = null)
{
    public const int FailuresBeforeSlowdown = 5;
    public static readonly TimeSpan Slowdown = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan UserFailureWindow = TimeSpan.FromMinutes(15);

    // Begrenzt den Speicher, falls jemand Millionen verschiedener Namen probiert: Darüber hinaus wird nichts Neues gemerkt.
    private const int MaxTrackedUsers = 5_000;
    private const int MaxLoggedNameLength = 32;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (int Count, DateTime Since)> _userFailures = [];

    public async Task<LoginResult> AttemptAsync(
        string? username, string? password, IPAddress? remote, IPAddress? local, CancellationToken ct = default)
    {
        // Die Middleware hat die Adresse beim Eintreffen geprüft; bei parallelen Versuchen kann die Sperre inzwischen stehen.
        var current = await blocks.CheckAsync(remote, local, ct);
        if (current.IsBlocked)
            return new LoginResult(null, BlockedMessage(current));

        var userKey = UserKey(username);
        if (userKey is not null && IsSlowedDown(userKey))
            await Task.Delay(Slowdown, time, ct);

        var result = await login.AuthenticateAsync(username, password, ct);
        // Passwort richtig, Code fehlt noch: weder Erfolg noch Fehlversuch. Erst der zweite Schritt entscheidet.
        if (result.RequiresSecondFactor)
            return result;
        if (result.Success)
        {
            if (userKey is not null)
                Forget(userKey);
            await blocks.RegisterSuccessAsync(remote, local, ct);
            if (audit is not null)
                await audit.WriteAsync(AuditActions.Login, AuthClaims.GetUserId(result.Principal!), result.Principal!.Identity?.Name, ip: Describe(remote), ct: ct);
            return result;
        }

        if (userKey is not null)
            Remember(userKey);
        var status = await blocks.RegisterFailureAsync(remote, local, ct);
        logger.LogWarning("LoginFailed ip={Address} user=\"{User}\"", Describe(remote), LogName(username));

        return status.IsBlocked ? new LoginResult(null, BlockedMessage(status)) : result;
    }

    /// <summary>
    /// Zweiter Schritt der Anmeldung. Ein falscher Code zählt wie ein falsches Passwort für die Sperre der Adresse, damit sich
    /// die sechs Ziffern nicht durchprobieren lassen.
    /// </summary>
    public async Task<LoginResult> AttemptSecondFactorAsync(
        PendingSecondFactor pending, string? code, IPAddress? remote, IPAddress? local, CancellationToken ct = default)
    {
        var current = await blocks.CheckAsync(remote, local, ct);
        if (current.IsBlocked)
            return new LoginResult(null, BlockedMessage(current));

        var userKey = UserKey(pending.Username);
        if (userKey is not null && IsSlowedDown(userKey))
            await Task.Delay(Slowdown, time, ct);

        var (result, kind) = await login.CompleteSecondFactorAsync(pending, code, ct);
        // Die Sperre des Kontos ist kein Fehlversuch dieser Adresse: Sonst bekäme ein Opfer, dessen Konto jemand gesperrt hat, auch noch die eigene Adress-Sperre.
        if (!result.Success && result.Error == Texts.Msg_Login_TooManyCodes)
            return result;
        if (result.Success)
        {
            if (userKey is not null)
                Forget(userKey);
            await blocks.RegisterSuccessAsync(remote, local, ct);
            if (audit is not null)
            {
                var detail = kind == SecondFactorKind.RecoveryCode ? "mit Wiederherstellungscode" : "mit Zwei-Faktor-Code";
                await audit.WriteAsync(AuditActions.Login, pending.UserId, pending.Username, detail: detail, ip: Describe(remote), ct: ct);
            }
            return result with { UsedRecoveryCode = kind == SecondFactorKind.RecoveryCode };
        }

        if (userKey is not null)
            Remember(userKey);
        var status = await blocks.RegisterFailureAsync(remote, local, ct);
        logger.LogWarning("LoginFailed ip={Address} user=\"{User}\" factor=2fa", Describe(remote), LogName(pending.Username));

        return status.IsBlocked ? new LoginResult(null, BlockedMessage(status)) : result;
    }

    public string BlockedMessage(IpBlockStatus status)
    {
        if (status.IsPermanent)
            return Texts.Msg_Login_BlockedPermanent;

        var remaining = status.UntilUtc!.Value - time.GetUtcNow().UtcDateTime;
        return string.Format(Texts.Culture, Texts.Msg_Login_BlockedUntil, FormatWait(remaining));
    }

    public static string FormatWait(TimeSpan remaining)
    {
        var minutes = (int)Math.Ceiling(Math.Max(remaining.TotalMinutes, 1));
        if (minutes < 60)
            return minutes == 1 ? Texts.Msg_Wait_OneMinute : string.Format(Texts.Culture, Texts.Msg_Wait_Minutes, minutes);

        var hours = (int)Math.Ceiling(minutes / 60.0);
        return hours == 1 ? Texts.Msg_Wait_OneHour : string.Format(Texts.Culture, Texts.Msg_Wait_Hours, hours);
    }

    /// <summary>Benutzername für das Log: Nur unschädliche Zeichen, damit niemand falsche Zeilen einschleusen kann (Zeilenumbruch, Anführungszeichen).</summary>
    public static string LogName(string? username)
    {
        var builder = new StringBuilder();
        foreach (var c in (username ?? "").Trim().Take(MaxLoggedNameLength))
            builder.Append(c < 128 && char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@' ? c : '?');
        return builder.ToString();
    }

    private static string Describe(IPAddress? address) =>
        address is null ? "unbekannt" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private static string? UserKey(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var trimmed = username.Trim().ToLowerInvariant();
        return trimmed[..Math.Min(trimmed.Length, 64)];
    }

    private bool IsSlowedDown(string userKey)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_gate)
            return _userFailures.TryGetValue(userKey, out var entry)
                   && now - entry.Since <= UserFailureWindow
                   && entry.Count >= FailuresBeforeSlowdown;
    }

    private void Remember(string userKey)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (_userFailures.TryGetValue(userKey, out var entry) && now - entry.Since <= UserFailureWindow)
            {
                _userFailures[userKey] = (entry.Count + 1, entry.Since);
                return;
            }

            if (_userFailures.Count >= MaxTrackedUsers)
                Purge(now);
            if (_userFailures.Count < MaxTrackedUsers)
                _userFailures[userKey] = (1, now);
        }
    }

    private void Forget(string userKey)
    {
        lock (_gate)
            _userFailures.Remove(userKey);
    }

    private void Purge(DateTime now)
    {
        foreach (var expired in _userFailures.Where(e => now - e.Value.Since > UserFailureWindow).Select(e => e.Key).ToList())
            _userFailures.Remove(expired);
    }
}
