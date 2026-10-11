using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Auth;

public static class AdminCheck
{
    /// <summary>Prüft die Admin-Rolle gegen die DB statt nur gegen die Claims im Cookie (die bis zur Revalidierung veraltet sein können).</summary>
    public static Task<bool> IsActiveAdminAsync(AppDbContext db, int userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin && !u.IsDisabled, ct);
}
