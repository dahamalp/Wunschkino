using System.Text.Json;

namespace Wunschkino.Anime;

/// <summary>Lädt die Zuordnungsliste beim Start von der Platte und danach täglich (mit ETag) von GitHub.</summary>
public sealed class AnimeIdMappingUpdater(
    IServiceScopeFactory scopeFactory,
    AnimeIdMapping mapping,
    AnimeListStore store,
    TimeProvider time,
    ILogger<AnimeIdMappingUpdater> logger) : BackgroundService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    // Eine Liste mit weniger Einträgen ist abgeschnitten oder hat ein anderes Format – dann bleibt der bisherige Stand.
    public const int MinPlausibleEntries = 100;

    // Die Liste wächst über die Jahre langsam. Schrumpft sie auf einen Schlag auf weniger als die Hälfte, ist eher die Quelle
    // kaputt oder manipuliert als der Bestand verschwunden.
    public const double MaxShrinkFactor = 0.5;

    private readonly SemaphoreSlim _trigger = new(0, 1);
    private int _runsCompleted;
    private volatile bool _lastRunSucceeded;

    /// <summary>Zahl der abgeschlossenen Läufe (ob erfolgreich oder nicht); die Admin-Seite wartet darauf, dass sie nach einem Anstoß steigt.</summary>
    public int RunsCompleted => Volatile.Read(ref _runsCompleted);

    public bool LastRunSucceeded => _lastRunSucceeded;

    private volatile bool _acceptShrink;

    /// <param name="acceptShrink">Vom Admin angestoßen: Dann gilt auch eine deutlich kleinere Liste, falls die Quelle wirklich geschrumpft ist.</param>
    public void RequestRefresh(bool acceptShrink = false)
    {
        if (acceptShrink)
            _acceptShrink = true;
        // Mehrfache Anstöße während eines Laufs fallen zu einem einzigen Folgelauf zusammen.
        try { _trigger.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadFromDisk();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = await RefreshOnceAsync(stoppingToken);
                await _trigger.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void LoadFromDisk()
    {
        try
        {
            if (store.ReadList() is not { } content)
                return;
            var entries = AnimeIdMapping.Parse(content);
            if (entries.Count < MinPlausibleEntries)
                return;
            mapping.Replace(entries, store.LastWriteUtc ?? time.GetUtcNow().UtcDateTime);
            logger.LogInformation("Anime-Zuordnungsliste von der Platte geladen: {Count} Einträge.", entries.Count);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Die gespeicherte Anime-Zuordnungsliste ist unbrauchbar und wird neu geladen.");
        }
    }

    /// <returns>Die Wartezeit bis zum nächsten Lauf.</returns>
    public async Task<TimeSpan> RefreshOnceAsync(CancellationToken ct)
    {
        var wait = await RefreshCoreAsync(ct);
        Interlocked.Increment(ref _runsCompleted);
        return wait;
    }

    private async Task<TimeSpan> RefreshCoreAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<AnimeListClient>();
        try
        {
            // Ohne geladene Liste (Datei fehlt oder war kaputt) darf der gespeicherte ETag nicht gesendet werden:
            // Ein „304 unverändert“ ließe das Mapping sonst bis zur nächsten Upstream-Änderung leer.
            var etag = mapping.Count > 0 ? store.ReadETag() : null;
            var download = await client.DownloadAsync(etag, ct);
            if (download.NotModified)
            {
                mapping.MarkChecked(time.GetUtcNow().UtcDateTime);
                _lastRunSucceeded = true;
                return RefreshInterval;
            }

            var entries = AnimeIdMapping.Parse(download.Content!);
            if (entries.Count < MinPlausibleEntries)
            {
                logger.LogWarning("Die Anime-Zuordnungsliste enthält nur {Count} nutzbare Einträge und wird verworfen.", entries.Count);
                _lastRunSucceeded = false;
                return RetryInterval;
            }
            var acceptShrink = _acceptShrink;
            _acceptShrink = false;
            if (!acceptShrink && mapping.Count > 0 && entries.Count < mapping.Count * MaxShrinkFactor)
            {
                logger.LogWarning("Die Anime-Zuordnungsliste ist von {Previous} auf {Count} Einträge geschrumpft und wird verworfen.", mapping.Count, entries.Count);
                _lastRunSucceeded = false;
                return RetryInterval;
            }

            store.Write(download.Content!, download.ETag);
            mapping.Replace(entries, time.GetUtcNow().UtcDateTime);
            logger.LogInformation("Anime-Zuordnungsliste aktualisiert: {Count} Einträge.", entries.Count);
            _lastRunSucceeded = true;
            return RefreshInterval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bewusst breit: Ein unerwarteter Fehler in diesem optionalen Feature darf nie den ganzen Host beenden.
            logger.LogWarning(ex, "Anime-Zuordnungsliste konnte nicht aktualisiert werden: {Reason}", ex.Message);
            _lastRunSucceeded = false;
            return RetryInterval;
        }
    }
}
