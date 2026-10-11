using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Auth;

/// <summary>Speichert die Darstellung (System, Hell, Dunkel) pro Konto in der Datenbank statt im Browser.</summary>
public sealed class ThemePreferenceService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<ThemePreference> GetAsync(int userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.ThemePreference)
            .SingleOrDefaultAsync(ct);
    }

    public async Task SetAsync(int userId, ThemePreference preference, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(preference))
            return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.ThemePreference, preference), ct);
    }
}
