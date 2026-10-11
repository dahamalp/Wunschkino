namespace Wunschkino.Hosting;

/// <summary>
/// Weist Anfragen ab, die eine andere Seite im Browser des Nutzers auslöst: am Blazor-Hub und bei allen Methoden, die etwas ändern.
/// <c>SameSite=Lax</c> hält echte Fremdseiten schon fern, nicht aber Geschwister-Subdomains derselben Domain. Browser senden bei solchen
/// Anfragen <c>Origin</c> oder <c>Sec-Fetch-Site</c> und lassen beides nicht von Seiten fälschen. Nicht-Browser (curl, Health-Checks)
/// schicken keinen von beiden und bleiben unberührt; sie brauchen den Schutz auch nicht, weil sie kein fremdes Cookie mitschicken.
/// </summary>
public sealed class OriginCheckMiddleware(RequestDelegate next, WunschkinoOptions? options = null)
{
    // Eingetragene Domains der App: Ein Origin mit einem dieser Hosts ist die App selbst, auch wenn der Proxy den Host-Header
    // umschreibt oder die öffentliche Adresse einen ungewöhnlichen Port hat.
    private readonly HashSet<string> _ownHosts = new(
        (options?.ResolveAllowedHosts() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (NeedsCheck(context.Request) && !IsSameSite(context.Request, _ownHosts))
        {
            // Mit Text, damit die Fehlerseiten-Umleitung die Antwort nicht durch eine erneute Ausführung des POST ersetzt.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Forbidden", context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static bool NeedsCheck(HttpRequest request) =>
        request.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
        || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method));

    public static bool IsSameSite(HttpRequest request, ISet<string>? ownHosts = null)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            // Verglichen werden nur Host und Port: Das Protokoll kennt die App hinter einem nicht eingetragenen Proxy womöglich falsch,
            // und ein Wechsel zwischen http und https auf demselben Host ist kein Angriffsweg dieser Art.
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && (string.Equals(Authority(uri.Host, uri.Port), Authority(request.Host.Host, request.Host.Port), StringComparison.OrdinalIgnoreCase)
                       || ownHosts?.Contains(uri.Host) == true);
        }

        var site = request.Headers["Sec-Fetch-Site"].ToString();
        return site.Length == 0 || site is "same-origin" or "none";
    }

    private static string Authority(string host, int? port) =>
        port is null or 80 or 443 ? host : $"{host}:{port}";
}
