namespace Wunschkino.Auth;

public static class ReturnUrl
{
    /// <summary>
    /// Lässt nur lokale Pfade zu, damit der Login nicht als Weiterleitung auf fremde Seiten missbraucht werden kann.
    /// Steuerzeichen und Backslashes sind ganz verboten: Browser entfernen z. B. Tabs, aus "/\t/evil.com" würde "//evil.com".
    /// </summary>
    public static string Sanitize(string? returnUrl) =>
        returnUrl is { Length: > 0 }
        && returnUrl[0] == '/'
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.Any(c => char.IsControl(c) || c == '\\')
        && !returnUrl.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            ? returnUrl
            : "/";
}
