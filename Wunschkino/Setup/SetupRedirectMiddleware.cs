namespace Wunschkino.Setup;

/// <summary>Leitet vor abgeschlossener Einrichtung alle Seitenaufrufe auf /setup um, danach /setup auf die Startseite.</summary>
public sealed class SetupRedirectMiddleware(RequestDelegate next, SetupState state)
{
    // „/about“ (Lizenz, Quelltext, Danksagung), „/impressum“ und „/datenschutz“ sind von der Fußzeile jeder Seite verlinkt und müssen auch vor der Einrichtung erreichbar sein.
    private static readonly string[] AlwaysAllowedPrefixes = ["/_framework", "/_content", "/_blazor", "/health", "/about", "/impressum", "/datenschutz", "/Error", "/not-found"];

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isSetupPath = path.StartsWithSegments("/setup", StringComparison.OrdinalIgnoreCase);

        if (state.IsCompleted)
        {
            if (isSetupPath)
            {
                context.Response.Redirect("/");
                return Task.CompletedTask;
            }
            return next(context);
        }

        if (isSetupPath || IsAlwaysAllowed(path))
            return next(context);

        context.Response.Redirect("/setup");
        return Task.CompletedTask;
    }

    private static bool IsAlwaysAllowed(PathString path) =>
        Path.HasExtension(path.Value)
        || AlwaysAllowedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
