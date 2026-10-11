using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Wunschkino.Data;

namespace Wunschkino.Auth;

/// <summary>
/// Prüft, ob ein Login-Cookie noch zum aktuellen Stand des Kontos passt (existiert, aktiv, gleiche Rolle, gleicher Security-Stamp)
/// und nicht länger als die erlaubte Gesamtdauer zurückliegt. Das Ergebnis der Kontoabfrage bleibt kurz im Speicher, denn jede
/// Anfrage mit Cookie fragt, auch die nach Bildern und Skripten; eine Änderung wirkt deshalb mit bis zu
/// <see cref="CacheLifetime"/> Verzögerung (Änderungen über die Konsole kommen aus einem anderen Prozess).
/// </summary>
public sealed class PrincipalValidator(
    IDbContextFactory<AppDbContext> dbFactory,
    WunschkinoOptions? options = null,
    TimeProvider? time = null,
    IMemoryCache? cache = null)
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _maxAge = TimeSpan.FromDays(options?.SessionMaxDays ?? WunschkinoOptions.DefaultSessionMaxDays);

    private sealed record Snapshot(string SecurityStamp, string Role, bool IsDisabled);

    public async Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (AuthClaims.GetUserId(principal) is not { } userId || !IsWithinMaxAge(principal))
            return false;

        var user = await LoadAsync(userId, principal.FindFirstValue(AuthClaims.SecurityStamp), ct);
        return user is { IsDisabled: false }
               && user.SecurityStamp == principal.FindFirstValue(AuthClaims.SecurityStamp)
               && user.Role == principal.FindFirstValue(ClaimTypes.Role);
    }

    // Fehlt die Angabe (Cookie aus einer älteren Version), gilt das Cookie als abgelaufen: lieber einmal neu anmelden.
    private bool IsWithinMaxAge(ClaimsPrincipal principal)
    {
        if (!long.TryParse(principal.FindFirstValue(AuthClaims.IssuedAt), out var seconds))
            return false;

        var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        return age <= _maxAge && age > TimeSpan.FromMinutes(-5);
    }

    // Der Stempel des Cookies gehört zum Schlüssel: Nach dem Abmelden oder einem Passwortwechsel trägt ein neu ausgestelltes Cookie
    // einen neuen Stempel und trifft den alten Eintrag nicht, sondern liest frisch aus der Datenbank.
    private async Task<Snapshot?> LoadAsync(int userId, string? cookieStamp, CancellationToken ct)
    {
        var key = $"principal:{userId}:{cookieStamp}";
        if (cache is not null && cache.TryGetValue(key, out Snapshot? cached))
            return cached;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.Role, u.IsDisabled }).SingleOrDefaultAsync(ct);
        var snapshot = user is null ? null : new Snapshot(user.SecurityStamp, user.Role.ToString(), user.IsDisabled);
        cache?.Set(key, snapshot, CacheLifetime);
        return snapshot;
    }
}
