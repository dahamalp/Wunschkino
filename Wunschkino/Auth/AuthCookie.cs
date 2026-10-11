using Microsoft.AspNetCore.Http;

namespace Wunschkino.Auth;

/// <summary>
/// Cookie-Namen. Das Präfix <c>__Host-</c> bindet ein Cookie fest an genau diese Domain (Secure, Pfad „/“, ohne Domain-Attribut):
/// Eine Geschwister-Subdomain kann es dann weder setzen noch überschreiben. Es geht nur mit Secure; in der Entwicklung über
/// http bleibt der einfache Name, sonst würde der Browser das Cookie verwerfen.
/// </summary>
public static class AuthCookie
{
    public const string Name = "Wunschkino.Auth";
    private const string HostPrefix = "__Host-";

    public static string NameFor(CookieSecurePolicy policy) => (policy == CookieSecurePolicy.Always ? HostPrefix : "") + Name;

    /// <summary>Wahr für den Anmelde-Cookie in beiden Schreibweisen (Tests und Prüfungen).</summary>
    public static bool IsAuthCookie(string name) => name == Name || name == HostPrefix + Name;
}
