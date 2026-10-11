using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Auth;

/// <summary>
/// Prüft vor Änderungen an Einstellungen und Sperrlisten noch einmal gegen die Datenbank, dass der Ausführende ein aktiver Admin ist.
/// Das [Authorize]-Attribut der Seite und das Cookie genügen dafür nicht allein: Das Cookie wird nur alle paar Sekunden gegen
/// die Datenbank geprüft, und ein Fehler in einer Seite darf die Dienste dahinter nicht öffnen.
/// </summary>
public sealed class AdminGuard(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<bool> IsAdminAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (AuthClaims.GetUserId(principal) is not { } userId)
            return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await AdminCheck.IsActiveAdminAsync(db, userId, ct);
    }
}
