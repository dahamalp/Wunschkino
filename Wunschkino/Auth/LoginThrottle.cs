using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Auth;

/// <summary>
/// Gemeinsame Fehlversuchs-Sperre für Login und Passwortänderung. Die Zähler werden per SQL atomar erhöht,
/// damit parallele Anfragen (Argon2 braucht spürbar Zeit) die Sperre nicht durch verlorene Updates umgehen.
/// </summary>
public static class LoginThrottle
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static bool IsLocked(User user, DateTime nowUtc) => user.LockedUntilUtc > nowUtc;

    public static async Task RegisterFailureAsync(AppDbContext db, int userId, DateTime nowUtc, CancellationToken ct)
    {
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedLogins, u => u.FailedLogins + 1), ct);

        var lockedUntil = nowUtc + LockoutDuration;
        await db.Users.Where(u => u.Id == userId && u.FailedLogins >= MaxFailedAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LockedUntilUtc, lockedUntil)
                .SetProperty(u => u.FailedLogins, 0), ct);
    }

    public static Task ResetAsync(AppDbContext db, int userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLogins, 0)
                .SetProperty(u => u.LockedUntilUtc, (DateTime?)null), ct);
}
