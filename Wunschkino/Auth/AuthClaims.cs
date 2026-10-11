using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Wunschkino.Data;

namespace Wunschkino.Auth;

public static class AuthClaims
{
    public const string SecurityStamp = "wunschkino:stamp";
    public const string AdminRole = nameof(UserRole.Admin);

    /// <summary>Zeitpunkt der Anmeldung (Unix-Sekunden). Die Gleitfrist des Cookies verlängert nur die Inaktivität; diese Angabe begrenzt die Gesamtdauer.</summary>
    public const string IssuedAt = "wunschkino:issued";

    /// <summary>Vorhanden, solange das Konto ein vom Admin vergebenes Passwort hat: Die App leitet dann zur Passwortänderung um.</summary>
    public const string MustChangePassword = "wunschkino:mustchange";

    public static bool MustChangePasswordNow(ClaimsPrincipal principal) => principal.HasClaim(c => c.Type == MustChangePassword);

    public static ClaimsPrincipal CreatePrincipal(User user, DateTimeOffset? issuedAt = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(SecurityStamp, user.SecurityStamp),
            new(IssuedAt, (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString())
        };
        if (user.MustChangePassword)
            claims.Add(new Claim(MustChangePassword, "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static int? GetUserId(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
