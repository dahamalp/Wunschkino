using System.Security.Cryptography;

namespace Wunschkino.Hosting;

/// <summary>
/// Setzt auf jede Antwort die Security-Header. Die Content-Security-Policy bekommt pro Anfrage ein Nonce für den einzigen
/// Inline-Skript-Block der Seite (die Import-Map von Blazor). Skripte dürfen sonst nur von der eigenen Herkunft kommen.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string NonceItemKey = "Wunschkino.CspNonce";

    public Task InvokeAsync(HttpContext context)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItemKey] = nonce;

        // OnStarting statt direkt zu setzen: Fehlerseiten-Middleware leert die Header einer Antwort beim Neuaufbau.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = BuildPolicy(nonce);
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers.XFrameOptions = "DENY";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        });
        return next(context);
    }

    public static string? GetNonce(HttpContext? context) =>
        context?.Items.TryGetValue(NonceItemKey, out var nonce) == true ? nonce as string : null;

    /// <remarks>
    /// <c>style-src 'unsafe-inline'</c> braucht MudBlazor für seine Inline-Styles. <c>connect-src 'self'</c> deckt die
    /// WebSocket-Verbindung der Blazor-Circuit zur eigenen Herkunft ab. Poster kommen über die eigenen Proxy-Endpoints.
    /// </remarks>
    public static string BuildPolicy(string nonce) =>
        "default-src 'self'; " +
        $"script-src 'self' 'nonce-{nonce}'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";
}
