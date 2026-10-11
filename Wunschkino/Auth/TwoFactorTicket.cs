using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Wunschkino.Auth;

/// <summary>
/// Merkt sich zwischen Passwort und Code, wer sich gerade anmeldet: verschlüsselt und signiert im Cookie, fünf Minuten gültig,
/// nur für die Anmeldeseiten. Ohne gültiges Ticket gibt es keinen zweiten Schritt, das Passwort muss erneut eingegeben werden.
/// Jedes Ticket gilt nur für eine erfolgreiche Anmeldung; ein abgefangenes Cookie lässt sich danach nicht mehr verwenden.
/// </summary>
public sealed class TwoFactorTicket(IDataProtectionProvider dataProtection, CookieSecurePolicy securePolicy)
{
    public const string CookieName = "Wunschkino.TwoFactor";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("Wunschkino.TwoFactorLogin.v1").ToTimeLimitedDataProtector();

    private readonly ConcurrentDictionary<string, DateTime> _consumed = new();

    public void Issue(HttpContext context, PendingSecondFactor pending, bool rememberMe)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var payload = JsonSerializer.Serialize(new Payload(id, pending.UserId, pending.Username, pending.SecurityStamp, rememberMe));
        context.Response.Cookies.Append(CookieName, _protector.Protect(payload, Lifetime), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = securePolicy == CookieSecurePolicy.Always || context.Request.IsHttps,
            Path = "/login",
            MaxAge = Lifetime,
            IsEssential = true
        });
    }

    public (PendingSecondFactor Pending, bool RememberMe, string Id)? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
            return null;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(value));
            if (payload is null || _consumed.ContainsKey(payload.Id))
                return null;
            return (new PendingSecondFactor(payload.UserId, payload.Username, payload.SecurityStamp), payload.RememberMe, payload.Id);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Entwertet das Ticket nach der Anmeldung. Die Liste muss nur so lange halten, wie ein Ticket überhaupt gültig ist.</summary>
    public void Consume(HttpContext context, string id)
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _consumed.Where(entry => now - entry.Value > Lifetime).Select(entry => entry.Key).ToList())
            _consumed.TryRemove(expired, out _);
        _consumed[id] = now;
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/login" });
    }

    private sealed record Payload(string Id, int UserId, string Username, string SecurityStamp, bool RememberMe);
}
