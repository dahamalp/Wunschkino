using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Hosting;

public static class HealthEndpoint
{
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(2);

    public static readonly string Version = ReadVersion();

    /// <summary>
    /// <c>GET /health</c>: anonym, auch vor der Einrichtung. Antwortet nur mit Status und Version und prüft, ob die Datenbank
    /// erreichbar ist – nichts aus dem Bestand oder der Konfiguration.
    /// </summary>
    public static IEndpointConventionBuilder MapHealth(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/health", async (HttpContext context, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(DatabaseTimeout);

            var healthy = false;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(timeout.Token);
                // Eine echte Abfrage statt CanConnect: Bei SQLite heißt das nur „Datei öffnen“ und legt eine gelöschte
                // Datenbank still leer neu an. So fällt auch ein fehlendes Schema auf.
                _ = await db.Settings.AsNoTracking().AnyAsync(timeout.Token);
                healthy = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
            {
                // Ungesund: DB nicht erreichbar, zu langsam oder ohne Schema. Details gehören nicht in die Antwort.
            }

            // Die Version erfährt nur, wer vom Server selbst fragt (das Install-Script). Öffentlich genügt „ok“.
            var local = ClientAddress.IsLocal(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
            var status = healthy ? "ok" : "unhealthy";
            object body = local ? new { status, version = Version } : new { status };
            return Results.Json(body, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

    private static string ReadVersion()
    {
        var informational = typeof(HealthEndpoint).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // „1.2.3+abc123“ → „1.2.3“: Der Commit-Hash gehört nicht in eine öffentliche Antwort.
        return informational?.Split('+')[0] ?? "unbekannt";
    }
}
