using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;
using Wunschkino.Resources;

namespace Wunschkino.Auth;

public sealed record UserSummary(int Id, string Username, UserRole Role, bool IsDisabled, DateTime? LockedUntilUtc, DateTime CreatedAtUtc, bool HasTwoFactor = false);

public sealed record UserOperationResult(bool Success, string? Error)
{
    public static UserOperationResult Ok() => new(true, null);
    public static UserOperationResult Fail(string error) => new(false, error);
}

/// <summary>
/// Benutzerverwaltung. Admin-Aktionen prüfen die Rolle des Ausführenden selbst noch einmal gegen die DB,
/// statt sich allein auf das [Authorize]-Attribut der Seite zu verlassen.
/// </summary>
public sealed class UserService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPasswordHasher passwordHasher,
    TimeProvider time,
    AuditLog? audit = null)
{
    private static string NotAllowed => Texts.Msg_User_NotAllowed;
    private static string UnknownUser => Texts.Msg_User_Unknown;
    private static string LastAdmin => Texts.Msg_User_LastAdmin;

    // Serialisiert Admin-Änderungen, damit zwei gleichzeitige Aktionen den letzten aktiven Admin nicht gemeinsam entfernen.
    private readonly SemaphoreSlim _adminChangeLock = new(1, 1);

    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserSummary(u.Id, u.Username, u.Role, u.IsDisabled, u.LockedUntilUtc, u.CreatedAtUtc, u.TotpEnabledUtc != null))
            .ToListAsync(ct);
    }

    public async Task<UserOperationResult> CreateAsync(int actingUserId, string username, string password, UserRole role, CancellationToken ct = default)
    {
        if ((UserValidation.ValidateUsername(username) ?? UserValidation.ValidatePassword(password)) is { } error)
            return UserOperationResult.Fail(error);

        var passwordHash = passwordHasher.Hash(password);

        await _adminChangeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await AdminCheck.IsActiveAdminAsync(db, actingUserId, ct))
                return UserOperationResult.Fail(NotAllowed);

            var trimmed = username.Trim();
            if (await db.Users.AnyAsync(u => u.Username == trimmed, ct))
                return UserOperationResult.Fail(Texts.Msg_User_Exists);

            db.Users.Add(new User
            {
                Username = trimmed,
                PasswordHash = passwordHash,
                MustChangePassword = true,
                Role = role,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime
            });
            await db.SaveChangesAsync(ct);
            await AuditAsync(db, actingUserId, AuditActions.UserCreated, trimmed, role.ToString(), ct);
            return UserOperationResult.Ok();
        }
        finally
        {
            _adminChangeLock.Release();
        }
    }

    public Task<UserOperationResult> ResetPasswordAsync(int actingUserId, int userId, string newPassword, CancellationToken ct = default)
    {
        if (UserValidation.ValidatePassword(newPassword) is { } error)
            return Task.FromResult(UserOperationResult.Fail(error));

        var passwordHash = passwordHasher.Hash(newPassword);
        return ModifyAsAdminAsync(actingUserId, userId, (_, user) =>
        {
            user.PasswordHash = passwordHash;
            user.MustChangePassword = true;
            user.FailedLogins = 0;
            user.LockedUntilUtc = null;
            return null;
        }, (AuditActions.UserPasswordReset, null), ct);
    }

    public Task<UserOperationResult> SetRoleAsync(int actingUserId, int userId, UserRole role, CancellationToken ct = default) =>
        ModifyAsAdminAsync(actingUserId, userId, (otherActiveAdmins, user) =>
        {
            if (role != UserRole.Admin && user is { Role: UserRole.Admin, IsDisabled: false } && otherActiveAdmins == 0)
                return LastAdmin;
            user.Role = role;
            return null;
        }, (AuditActions.UserRole, role.ToString()), ct);

    public Task<UserOperationResult> SetDisabledAsync(int actingUserId, int userId, bool disabled, CancellationToken ct = default) =>
        ModifyAsAdminAsync(actingUserId, userId, (otherActiveAdmins, user) =>
        {
            if (disabled && user.Id == actingUserId)
                return Texts.Msg_User_CannotDisableSelf;
            if (disabled && user is { Role: UserRole.Admin, IsDisabled: false } && otherActiveAdmins == 0)
                return LastAdmin;
            user.IsDisabled = disabled;
            return null;
        }, (disabled ? AuditActions.UserDisabled : AuditActions.UserEnabled, null), ct);

    public Task<UserOperationResult> UnlockAsync(int actingUserId, int userId, CancellationToken ct = default) =>
        ModifyAsAdminAsync(actingUserId, userId, (_, user) =>
        {
            user.FailedLogins = 0;
            user.LockedUntilUtc = null;
            return null;
        }, (AuditActions.UserUnlocked, null), ct, rotateSecurityStamp: false);

    public async Task<UserOperationResult> ChangeOwnPasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (UserValidation.ValidatePassword(newPassword) is { } error)
            return UserOperationResult.Fail(error);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.IsDisabled)
            return UserOperationResult.Fail(UnknownUser);
        if (await CheckCurrentPasswordAsync(db, user, currentPassword, ct) is { } passwordError)
            return UserOperationResult.Fail(passwordError);

        user.PasswordHash = passwordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        user.SecurityStamp = User.NewSecurityStamp();
        user.FailedLogins = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync(ct);
        if (audit is not null)
            await audit.WriteAsync(AuditActions.PasswordChanged, user.Id, user.Username, ct: ct);
        return UserOperationResult.Ok();
    }

    /// <summary>Macht alle bestehenden Anmelde-Cookies des Kontos ungültig, auch gestohlene Kopien (Abmelden, 2FA-Änderungen).</summary>
    public async Task RevokeSessionsAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stamp = User.NewSecurityStamp();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, stamp), ct);
    }

    /// <summary>
    /// Verlangt das Passwort für heikle Änderungen noch einmal. Ein gestohlenes Cookie reicht dann nicht, um Zugänge, Rechte oder
    /// die Verbindung zu Jellyfin umzubauen.
    /// </summary>
    public async Task<UserOperationResult> ConfirmPasswordAsync(int userId, string? password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.IsDisabled)
            return UserOperationResult.Fail(UnknownUser);

        return await CheckCurrentPasswordAsync(db, user, password, ct) is { } error
            ? UserOperationResult.Fail(error)
            : UserOperationResult.Ok();
    }

    // Dieselbe Sperre wie beim Login, damit ein gestohlenes Cookie nicht zum Durchprobieren des Passworts taugt.
    private async Task<string?> CheckCurrentPasswordAsync(AppDbContext db, User user, string? password, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (LoginThrottle.IsLocked(user, now))
            return Texts.Msg_TooManyAttempts;
        if (string.IsNullOrEmpty(password) || password.Length > UserValidation.MaxPasswordLength
            || !passwordHasher.Verify(password, user.PasswordHash))
        {
            await LoginThrottle.RegisterFailureAsync(db, user.Id, now, ct);
            return Texts.Msg_User_CurrentPasswordWrong;
        }

        // Frühere Tippfehler sollen sich nicht über mehrere Bestätigungen zu einer Sperre summieren.
        if (user.FailedLogins > 0)
            await LoginThrottle.ResetAsync(db, user.Id, ct);
        return null;
    }

    private async Task AuditAsync(AppDbContext db, int actingUserId, string action, string? target, string? detail, CancellationToken ct)
    {
        if (audit is null)
            return;

        var actorName = await db.Users.AsNoTracking().Where(u => u.Id == actingUserId).Select(u => u.Username).SingleOrDefaultAsync(ct);
        await audit.WriteAsync(action, actingUserId, actorName, target, detail, ct: ct);
    }

    /// <param name="change">Bekommt die Zahl der übrigen aktiven Admins (ohne den Zielnutzer) und liefert eine Fehlermeldung oder null.</param>
    private async Task<UserOperationResult> ModifyAsAdminAsync(
        int actingUserId,
        int userId,
        Func<int, User, string?> change,
        (string Action, string? Detail) auditEvent,
        CancellationToken ct,
        bool rotateSecurityStamp = true)
    {
        await _adminChangeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await AdminCheck.IsActiveAdminAsync(db, actingUserId, ct))
                return UserOperationResult.Fail(NotAllowed);

            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
                return UserOperationResult.Fail(UnknownUser);

            var otherActiveAdmins = await db.Users.CountAsync(u => u.Id != userId && u.Role == UserRole.Admin && !u.IsDisabled, ct);
            if (change(otherActiveAdmins, user) is { } error)
                return UserOperationResult.Fail(error);

            if (rotateSecurityStamp)
                user.SecurityStamp = User.NewSecurityStamp();
            await db.SaveChangesAsync(ct);
            await AuditAsync(db, actingUserId, auditEvent.Action, user.Username, auditEvent.Detail, ct);
            return UserOperationResult.Ok();
        }
        finally
        {
            _adminChangeLock.Release();
        }
    }
}
