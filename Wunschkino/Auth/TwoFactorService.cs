using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using System.Text;
using Wunschkino.Data;
using Wunschkino.Resources;
using Wunschkino.Services;

namespace Wunschkino.Auth;

public sealed record TwoFactorSetup(byte[] Secret, string SecretText, string QrCodeDataUri);

public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

public sealed record TwoFactorResult(bool Success, string? Error, IReadOnlyList<string>? RecoveryCodes = null)
{
    public static TwoFactorResult Fail(string error) => new(false, error);
}

public enum SecondFactorKind
{
    None,
    Totp,
    RecoveryCode
}

/// <summary>
/// Optionale Zwei-Faktor-Anmeldung pro Konto mit TOTP und Wiederherstellungscodes. Der Schlüssel liegt mit Data Protection
/// verschlüsselt in der DB, die Wiederherstellungscodes nur als HMAC mit einem geheimen Serverschlüssel (ältere Codes als Argon2-Hash,
/// bis sie benutzt werden). Der QR-Code entsteht auf dem Server: Kein fremder
/// Dienst sieht den Schlüssel.
/// </summary>
public sealed class TwoFactorService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    IPasswordHasher passwordHasher,
    SettingsService settings,
    TimeProvider time,
    AuditLog? audit = null)
{
    public const string Issuer = "Wunschkino";
    public const int RecoveryCodeCount = 10;

    // Fehlversuche auf der Kontoseite sperren nur diese Seite, nie die Anmeldung: Wer ein fremdes Cookie hat, soll den
    // Besitzer nicht aussperren können.
    public const int MaxManagementFailures = 5;
    public static readonly TimeSpan ManagementFailureWindow = TimeSpan.FromMinutes(15);

    // Ohne verwechselbare Zeichen (0/O, 1/I/L), damit sich die Codes vom Papier abtippen lassen.
    private const string RecoveryAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int RecoveryCodeLength = 10;

    private static string WrongCode => Texts.Msg_TwoFactor_WrongCode;
    private static string TooManyAttempts => Texts.Msg_TooManyAttempts;

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Wunschkino.Totp.v1");
    private readonly ConcurrentDictionary<int, (int Count, DateTime Since)> _managementFailures = new();

    public TwoFactorSetup CreateSetup(string username)
    {
        var secret = Totp.GenerateSecret();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Totp.BuildUri(Issuer, username, secret), QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        var grouped = string.Join(' ', Totp.ToBase32(secret).Chunk(4).Select(chunk => new string(chunk)));
        return new TwoFactorSetup(secret, grouped, "data:image/png;base64," + Convert.ToBase64String(png));
    }

    public async Task<TwoFactorStatus> GetStatusAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var enabled = await db.Users.AnyAsync(u => u.Id == userId && u.TotpEnabledUtc != null, ct);
        var left = enabled ? await db.RecoveryCodes.CountAsync(c => c.UserId == userId, ct) : 0;
        return new TwoFactorStatus(enabled, left);
    }

    /// <summary>
    /// Schaltet TOTP ein, sobald Passwort und ein Code aus der App passen, und liefert die Wiederherstellungscodes (nur dieses
    /// eine Mal). Das Passwort verhindert, dass ein gestohlenes Cookie den Besitzer mit einem fremden Handy aussperrt.
    /// </summary>
    public async Task<TwoFactorResult> EnableAsync(int userId, string? password, byte[] secret, string? code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDisabled, ct);
        if (user is null)
            return TwoFactorResult.Fail(Texts.Msg_User_Unknown);
        if (user.HasTwoFactor)
            return TwoFactorResult.Fail(Texts.Msg_TwoFactor_AlreadyOn);
        if (IsManagementLocked(userId))
            return TwoFactorResult.Fail(TooManyAttempts);
        if (!PasswordMatches(password, user))
            return Failure(userId, Texts.Msg_TwoFactor_PasswordWrong);

        var now = time.GetUtcNow();
        if (Totp.FindMatchingStep(secret, code, now, lastUsedStep: 0) is not { } step)
            return Failure(userId, WrongCode);

        var codes = NewRecoveryCodes();
        var recoveryKey = await RecoveryKeyAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Bedingt, damit zwei offene Tabs nicht nacheinander zwei verschiedene Schlüssel einschalten.
        // Der Stempel bleibt bewusst gleich: Ein neuer würde auch die Sitzung beenden, in der der Nutzer gerade die Codes ansieht, und aus
        // einer Blazor-Verbindung lässt sich kein neues Cookie ausstellen. Wer fremde Sitzungen beenden will, meldet sich ab.
        var enabled = await db.Users.Where(u => u.Id == userId && u.TotpEnabledUtc == null).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.TotpSecret, _protector.Protect(Totp.ToBase32(secret)))
            .SetProperty(u => u.TotpEnabledUtc, now.UtcDateTime)
            .SetProperty(u => u.TotpLastTimeStep, step), ct);
        if (enabled != 1)
            return TwoFactorResult.Fail(Texts.Msg_TwoFactor_AlreadyOn);
        db.RecoveryCodes.AddRange(HashAll(recoveryKey, userId, codes));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        ForgetFailures(userId);
        await WriteAuditAsync(AuditActions.TwoFactorEnabled, user, ct);
        return new TwoFactorResult(true, null, codes);
    }

    /// <summary>Abschalten verlangt Passwort und einen aktuellen Code (oder einen Wiederherstellungscode).</summary>
    public async Task<TwoFactorResult> DisableAsync(int userId, string? password, string? code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDisabled, ct);
        if (user is not { HasTwoFactor: true })
            return TwoFactorResult.Fail(Texts.Msg_TwoFactor_NotOn);
        if (IsManagementLocked(userId))
            return TwoFactorResult.Fail(TooManyAttempts);
        if (!PasswordMatches(password, user))
            return Failure(userId, Texts.Msg_TwoFactor_PasswordWrong);
        if (await VerifyCoreAsync(db, user, code, ct) == SecondFactorKind.None)
            return Failure(userId, WrongCode);

        await ClearAsync(db, user.Id, rotateSecurityStamp: false, ct);
        ForgetFailures(userId);
        await WriteAuditAsync(AuditActions.TwoFactorDisabled, user, ct);
        return new TwoFactorResult(true, null);
    }

    public async Task<TwoFactorResult> RegenerateRecoveryCodesAsync(int userId, string? code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.IsDisabled, ct);
        if (user is not { HasTwoFactor: true })
            return TwoFactorResult.Fail(Texts.Msg_TwoFactor_NotOn);
        if (IsManagementLocked(userId))
            return TwoFactorResult.Fail(TooManyAttempts);
        // Nur ein Code aus der App: Mit einem Wiederherstellungscode ließen sich sonst beliebig neue erzeugen.
        if (Totp.Normalize(code) is null || await VerifyCoreAsync(db, user, code, ct) != SecondFactorKind.Totp)
            return Failure(userId, WrongCode);

        var codes = NewRecoveryCodes();
        var recoveryKey = await RecoveryKeyAsync(ct);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(ct);
            db.RecoveryCodes.AddRange(HashAll(recoveryKey, user.Id, codes));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        ForgetFailures(userId);
        await WriteAuditAsync(AuditActions.RecoveryCodesRenewed, user, ct);
        return new TwoFactorResult(true, null, codes);
    }

    /// <summary>Prüft den zweiten Faktor bei der Anmeldung. Jeder Code gilt nur einmal.</summary>
    public async Task<SecondFactorKind> VerifyAsync(int userId, string? code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is { HasTwoFactor: true } ? await VerifyCoreAsync(db, user, code, ct) : SecondFactorKind.None;
    }

    /// <summary>Schaltet TOTP für ein anderes Konto ab (Handy verloren). Meldet das Konto überall ab.</summary>
    public async Task<UserOperationResult> ResetByAdminAsync(int actingUserId, int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AdminCheck.IsActiveAdminAsync(db, actingUserId, ct))
            return UserOperationResult.Fail(Texts.Msg_User_NotAllowed);
        if (actingUserId == userId)
            return UserOperationResult.Fail(Texts.Msg_TwoFactor_ResetSelf);

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is not { HasTwoFactor: true })
            return UserOperationResult.Fail(Texts.Msg_TwoFactor_ResetNotOn);

        await ClearAsync(db, user.Id, rotateSecurityStamp: true, ct);
        ForgetFailures(userId);
        if (audit is not null)
        {
            var actorName = await db.Users.AsNoTracking().Where(u => u.Id == actingUserId).Select(u => u.Username).SingleOrDefaultAsync(ct);
            await audit.WriteAsync(AuditActions.TwoFactorReset, actingUserId, actorName, user.Username, ct: ct);
        }
        return UserOperationResult.Ok();
    }

    /// <summary>
    /// Schaltet TOTP in einer Transaktion ab: Ein Abbruch mittendrin darf kein Konto mit 2FA, aber ohne Wiederherstellungscodes
    /// hinterlassen. Mit <paramref name="rotateSecurityStamp"/> wird das Konto zusätzlich überall abgemeldet.
    /// </summary>
    public static async Task ClearAsync(AppDbContext db, int userId, bool rotateSecurityStamp, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.RecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        var users = db.Users.Where(u => u.Id == userId);
        if (rotateSecurityStamp)
        {
            var stamp = User.NewSecurityStamp();
            await users.ExecuteUpdateAsync(s => s
                .SetProperty(u => u.TotpSecret, (string?)null)
                .SetProperty(u => u.TotpEnabledUtc, (DateTime?)null)
                .SetProperty(u => u.TotpLastTimeStep, 0L)
                .SetProperty(u => u.SecurityStamp, stamp), ct);
        }
        else
        {
            await users.ExecuteUpdateAsync(s => s
                .SetProperty(u => u.TotpSecret, (string?)null)
                .SetProperty(u => u.TotpEnabledUtc, (DateTime?)null)
                .SetProperty(u => u.TotpLastTimeStep, 0L), ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static string NormalizeRecoveryCode(string code) =>
        new(code.ToUpperInvariant().Where(c => c is not (' ' or '-')).ToArray());

    private async Task<SecondFactorKind> VerifyCoreAsync(AppDbContext db, User user, string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            return SecondFactorKind.None;

        if (Totp.Normalize(code) is not null)
        {
            if (Unprotect(user.TotpSecret) is not { } secret
                || Totp.FindMatchingStep(secret, code, time.GetUtcNow(), user.TotpLastTimeStep) is not { } step)
                return SecondFactorKind.None;

            // Bedingtes Update statt Lesen und Schreiben: Von zwei gleichzeitigen Anmeldungen mit demselben Code gewinnt nur eine.
            var updated = await db.Users.Where(u => u.Id == user.Id && u.TotpLastTimeStep < step)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpLastTimeStep, step), ct);
            return updated == 1 ? SecondFactorKind.Totp : SecondFactorKind.None;
        }

        // Ältere Codes sind Argon2-Hashes mit Salz und lassen sich nicht per Gleichheit suchen: höchstens zehn Vergleiche.
        var normalized = NormalizeRecoveryCode(code);
        var recoveryKey = await RecoveryKeyAsync(ct);
        var candidates = await db.RecoveryCodes.AsNoTracking().Where(c => c.UserId == user.Id).ToListAsync(ct);
        var match = candidates.FirstOrDefault(c => MatchesRecoveryCode(recoveryKey, normalized, c.CodeHash));
        if (match is null)
            return SecondFactorKind.None;
        var deleted = await db.RecoveryCodes.Where(c => c.Id == match.Id).ExecuteDeleteAsync(ct);
        return deleted == 1 ? SecondFactorKind.RecoveryCode : SecondFactorKind.None;
    }

    private bool PasswordMatches(string? password, User user) =>
        !string.IsNullOrEmpty(password) && password.Length <= UserValidation.MaxPasswordLength && passwordHasher.Verify(password, user.PasswordHash);

    private bool IsManagementLocked(int userId) =>
        _managementFailures.TryGetValue(userId, out var entry)
        && time.GetUtcNow().UtcDateTime - entry.Since <= ManagementFailureWindow
        && entry.Count >= MaxManagementFailures;

    private TwoFactorResult Failure(int userId, string message)
    {
        var now = time.GetUtcNow().UtcDateTime;
        _managementFailures.AddOrUpdate(userId, (1, now),
            (_, entry) => now - entry.Since <= ManagementFailureWindow ? (entry.Count + 1, entry.Since) : (1, now));
        return TwoFactorResult.Fail(message);
    }

    private void ForgetFailures(int userId) => _managementFailures.TryRemove(userId, out _);

    private byte[]? Unprotect(string? protectedSecret)
    {
        if (protectedSecret is null)
            return null;
        try
        {
            return Totp.FromBase32(_protector.Unprotect(protectedSecret));
        }
        catch (CryptographicException)
        {
            // Schlüsselring verloren oder getauscht: Dann hilft nur ein Wiederherstellungscode oder das Zurücksetzen.
            return null;
        }
    }

    private static List<string> NewRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        while (codes.Count < RecoveryCodeCount)
        {
            var raw = RandomNumberGenerator.GetString(RecoveryAlphabet, RecoveryCodeLength);
            var code = $"{raw[..5]}-{raw[5..]}";
            if (!codes.Contains(code))
                codes.Add(code);
        }
        return codes;
    }

    private const string HmacPrefix = "h1:";
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private byte[]? _recoveryKey;

    // Die Codes haben nur rund 49 Bit Zufall. Ein HMAC mit einem Schlüssel, der nicht in der Datenbank liegt (verschlüsselt über
    // Data Protection), lässt sich aus einem Datenbankabzug nicht durchprobieren; Argon2 wäre dafür bei jeder Anmeldung teuer.
    private async Task<byte[]> RecoveryKeyAsync(CancellationToken ct)
    {
        if (_recoveryKey is { } cached)
            return cached;

        await _keyGate.WaitAsync(ct);
        try
        {
            if (_recoveryKey is null)
            {
                var stored = await settings.GetAsync(SettingKeys.RecoveryCodeKey, ct);
                if (stored is null)
                {
                    stored = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                    await settings.SetAsync(SettingKeys.RecoveryCodeKey, stored, isProtected: true, ct);
                }
                _recoveryKey = Convert.FromBase64String(stored);
            }
            return _recoveryKey;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private static string HashRecoveryCode(byte[] key, string normalizedCode) =>
        HmacPrefix + Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalizedCode)));

    private bool MatchesRecoveryCode(byte[] key, string normalizedCode, string stored) =>
        stored.StartsWith(HmacPrefix, StringComparison.Ordinal)
            ? CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashRecoveryCode(key, normalizedCode)), Encoding.ASCII.GetBytes(stored))
            : passwordHasher.Verify(normalizedCode, stored);

    private static IEnumerable<RecoveryCode> HashAll(byte[] key, int userId, IEnumerable<string> codes) =>
        codes.Select(code => new RecoveryCode { UserId = userId, CodeHash = HashRecoveryCode(key, NormalizeRecoveryCode(code)) }).ToList();

    private async Task WriteAuditAsync(string action, User user, CancellationToken ct)
    {
        if (audit is not null)
            await audit.WriteAsync(action, user.Id, user.Username, ct: ct);
    }
}
