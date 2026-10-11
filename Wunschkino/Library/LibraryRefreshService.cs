using Wunschkino.Gaps;
using Wunschkino.Resources;
using Wunschkino.Services;
using Wunschkino.Wishes;

namespace Wunschkino.Library;

/// <summary>Lädt den Jellyfin-Bestand im eingestellten Intervall neu; Admin-Aktionen können einen sofortigen Lauf anstoßen.</summary>
public sealed class LibraryRefreshService(
    IServiceScopeFactory scopeFactory,
    LibraryCache cache,
    WishAutoCompleter wishAutoCompleter,
    TimeProvider time,
    ILogger<LibraryRefreshService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _trigger = new(0, 1);

    public void RequestRefresh()
    {
        // Mehrfache Anstöße während eines Laufs fallen zu einem einzigen Folgelauf zusammen.
        try { _trigger.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = await RefreshOnceAsync(stoppingToken);
            try
            {
                await _trigger.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns>Die Wartezeit bis zum nächsten regulären Lauf.</returns>
    public async Task<TimeSpan> RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            return await RefreshLibraryAsync(ct);
        }
        finally
        {
            await RefreshWishMetadataAsync(ct);
        }
    }

    private async Task<TimeSpan> RefreshLibraryAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var connectionSettings = scope.ServiceProvider.GetRequiredService<ConnectionSettingsService>();
        var jellyfin = scope.ServiceProvider.GetRequiredService<JellyfinClient>();

        var interval = TimeSpan.FromMinutes(await connectionSettings.GetRefreshMinutesAsync(ct));
        var url = await settings.GetAsync(SettingKeys.JellyfinUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.JellyfinApiKey, ct);
        if (url is null || apiKey is null)
        {
            cache.ReportError(Texts.Msg_Library_NotConfigured);
            return interval;
        }

        try
        {
            var items = await jellyfin.GetLibraryAsync(url, apiKey, ct);
            cache.Update(items, time.GetUtcNow().UtcDateTime);
            logger.LogInformation("Jellyfin-Bibliothek aktualisiert: {Count} Titel.", items.Count);
            await CompleteWishesAsync(items, ct);
            await CheckSeriesGapsAsync(scope.ServiceProvider, items, ct);
        }
        catch (JellyfinException ex)
        {
            cache.ReportError(ex.Message);
            logger.LogWarning("Jellyfin-Bibliothek konnte nicht geladen werden: {Reason}", ex.Message);
        }
        return interval;
    }

    /// <summary>Ein Fehler hier darf den Bestands-Refresh nicht abbrechen: Die Folgen-Prüfung ist eine Zusatzfunktion.</summary>
    private async Task CheckSeriesGapsAsync(IServiceProvider services, IReadOnlyList<LibraryItem> items, CancellationToken ct)
    {
        try
        {
            await services.GetRequiredService<SeriesGapService>().CheckDueAsync(items, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Die Prüfung auf fehlende Folgen ist fehlgeschlagen.");
        }
    }

    /// <summary>Unabhängig von Jellyfin: Auch ohne erreichbaren Jellyfin-Server dürfen die TMDB-Daten der Wünsche nicht veralten.</summary>
    private async Task RefreshWishMetadataAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WishMetadataRefresher>().RefreshStaleAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Die Auffrischung der TMDB-Daten der Wünsche ist fehlgeschlagen.");
        }
    }

    /// <summary>Ein Fehler hier darf den Bestands-Refresh nicht abbrechen: Der Bestand ist längst gültig im Cache.</summary>
    private async Task CompleteWishesAsync(IReadOnlyList<LibraryItem> items, CancellationToken ct)
    {
        try
        {
            await wishAutoCompleter.CompleteAsync(items, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Der Abgleich der Wünsche mit dem Jellyfin-Bestand ist fehlgeschlagen.");
        }
    }
}
