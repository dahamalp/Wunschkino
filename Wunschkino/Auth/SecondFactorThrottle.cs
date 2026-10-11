using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Auth;

/// <summary>
/// Sperre für falsche Codes im zweiten Schritt der Anmeldung, je Konto und unabhängig von der Adresse: Wer das Passwort kennt, soll
/// die sechs Ziffern auch nicht über viele Adressen verteilt durchprobieren können. Die Zähler liegen in der Datenbank, ein
/// Neustart hebt die Sperre also nicht auf. Nach je zehn Fehlversuchen wächst die Sperre (15 Minuten, 1, 6, dann 24 Stunden);
/// ein richtiger Code oder sieben Tage ohne Fehlversuch setzen den Zähler zurück. Die Zähler werden per SQL erhöht, damit
/// parallele Anfragen die Sperre nicht durch verlorene Updates umgehen.
/// </summary>
public static class SecondFactorThrottle
{
    public const int FailuresPerStage = 10;
    public static readonly TimeSpan Decay = TimeSpan.FromDays(7);
    public static readonly IReadOnlyList<TimeSpan> Stages =
        [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    public static bool IsLocked(User user, DateTime nowUtc) => user.SecondFactorLockedUntilUtc > nowUtc;

    public static async Task RegisterFailureAsync(AppDbContext db, int userId, DateTime nowUtc, CancellationToken ct)
    {
        var stale = nowUtc - Decay;
        await db.Users.Where(u => u.Id == userId && u.SecondFactorLastFailureUtc < stale)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecondFactorFailures, 0), ct);
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.SecondFactorFailures, u => u.SecondFactorFailures + 1)
                .SetProperty(u => u.SecondFactorLastFailureUtc, nowUtc), ct);

        var state = await db.Users.Where(u => u.Id == userId)
            .Select(u => new { u.SecondFactorFailures, u.SecondFactorLockedUntilUtc }).SingleOrDefaultAsync(ct);
        if (state is null)
            return;

        // Gesperrt wird bei jedem vollen Zehnerschritt; die erste Sperre kommt auch dann, wenn parallele Versuche einen Schritt
        // übersprungen haben. Nach Ablauf einer Sperre hat der Nutzer wieder zehn Versuche bis zur nächsten, längeren.
        var failures = state.SecondFactorFailures;
        if (failures < FailuresPerStage || (failures % FailuresPerStage != 0 && state.SecondFactorLockedUntilUtc is not null))
            return;

        var lockedUntil = nowUtc + Stages[Math.Min(failures / FailuresPerStage, Stages.Count) - 1];
        await db.Users.Where(u => u.Id == userId && (u.SecondFactorLockedUntilUtc == null || u.SecondFactorLockedUntilUtc <= nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecondFactorLockedUntilUtc, lockedUntil), ct);
    }

    public static Task ResetAsync(AppDbContext db, int userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId && (u.SecondFactorFailures != 0 || u.SecondFactorLockedUntilUtc != null))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.SecondFactorFailures, 0)
                .SetProperty(u => u.SecondFactorLockedUntilUtc, (DateTime?)null), ct);
}
