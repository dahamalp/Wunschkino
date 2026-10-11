using Wunschkino.Auth;
using Wunschkino.Resources;

namespace Wunschkino.Hosting;

/// <summary>Weist gesperrte Absender-Adressen früh und billig ab, bevor Blazor, Datenbank oder Passwort-Hashing Arbeit machen.</summary>
public sealed class IpBlockMiddleware(
    RequestDelegate next,
    IpBlockService blocks,
    TimeProvider time,
    ILogger<IpBlockMiddleware> logger,
    ProxyWarningState? proxyWarning = null)
{
    private static readonly TimeSpan WarnInterval = TimeSpan.FromHours(1);

    private long _lastWarnedTicks;

    /// <summary>
    /// Der Browser setzt <c>Sec-Fetch-Site: cross-site</c>, wenn eine fremde Seite die Anfrage auslöst (etwa Bilder oder Formulare
    /// auf einer Angreiferseite). Eine Seite kann den Header nicht fälschen. Solche Anfragen dürfen niemanden sperren.
    /// </summary>
    public static bool IsCrossSite(HttpRequest request) =>
        string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        WarnIfProxyIsNotTrusted(context);
        NoteLoopbackProxyWithoutForwarding(context);
        var status = await blocks.CheckAsync(
            context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress, context.RequestAborted);
        if (!status.IsBlocked)
        {
            await next(context);
            return;
        }

        context.Response.ContentType = "text/plain; charset=utf-8";
        if (status.IsPermanent)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(Texts.Msg_Block_Permanent, context.RequestAborted);
            return;
        }

        var remaining = status.UntilUtc!.Value - time.GetUtcNow().UtcDateTime;
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(remaining.TotalSeconds, 1))).ToString();
        await context.Response.WriteAsync(
            string.Format(Texts.Culture, Texts.Msg_Block_Temporary, LoginGuard.FormatWait(remaining)), context.RequestAborted);
    }

    // Ein Proxy auf demselben Server ohne X-Forwarded-For lässt jeden Besucher wie „lokal“ aussehen, und lokale Adressen sind von
    // Sperre und Anfrage-Limit ausgenommen. Erkennbar daran, dass die Anfrage von Loopback kommt, aber einen fremden Host-Namen trägt:
    // Wer direkt am Server arbeitet (Health-Check, curl), nennt localhost oder eine Loopback-Adresse.
    private void NoteLoopbackProxyWithoutForwarding(HttpContext context)
    {
        if (proxyWarning is null || !ClientAddress.IsLocal(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress))
            return;
        var headers = context.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Original-For"))
            return;

        var host = context.Request.Host.Host;
        var isLoopbackHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                             || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
        if (!isLoopbackHost)
            proxyWarning.RecordLoopbackWithoutForwarding(time.GetUtcNow().UtcDateTime);
    }

    // Nach UseForwardedHeaders bleibt X-Forwarded-For nur stehen, wenn der Absender kein vertrauter Proxy ist (sonst steht dort
    // X-Original-For). Dann sähen alle Nutzer hinter ihm wie eine Adresse aus und teilten sich Limit und Sperre.
    private void WarnIfProxyIsNotTrusted(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey("X-Forwarded-For") || context.Request.Headers.ContainsKey("X-Original-For"))
            return;
        if (blocks.IsExempt(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress))
            return;

        // Für die Sicherheitscheck-Seite bei jedem Fund merken; das Log bekommt die Zeile nur einmal pro Stunde.
        proxyWarning?.Record(context.Connection.RemoteIpAddress, time.GetUtcNow().UtcDateTime);

        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWarnedTicks);
        if (last != 0 && now - last < WarnInterval.Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastWarnedTicks, now, last) != last)
            return;

        logger.LogWarning(
            "ProxyNotTrusted remote={Address}: Diese Adresse schickt X-Forwarded-For, ist aber nicht als Proxy eingetragen (--proxy). " +
            "Alle Nutzer hinter ihr teilen sich Login-Limit und Sperre; ein Angreifer könnte sie für alle sperren lassen.",
            context.Connection.RemoteIpAddress);
    }
}
