using Wunschkino.Auth;

namespace Wunschkino.Hosting;

/// <summary>
/// Leitet Seitenaufrufe eines Kontos, dessen Passwort ein Admin vergeben hat, zur Passwortänderung um. Das ist Komfort und Hygiene,
/// keine Zugriffssperre: Das Passwort kennt der Admin ohnehin. Bilder, Skripte und die Blazor-Verbindung bleiben unberührt.
/// </summary>
public sealed class PasswordChangeMiddleware(RequestDelegate next)
{
    public const string PasswordPath = "/account/password";

    private static readonly string[] AllowedPrefixes = [PasswordPath, "/account/logout", "/login", "/health", "/_blazor", "/_framework", "/_content"];

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && AuthClaims.MustChangePasswordNow(context.User)
            && HttpMethods.IsGet(context.Request.Method)
            && IsPageNavigation(context.Request)
            && !AllowedPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.Redirect(PasswordPath);
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsPageNavigation(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
}
