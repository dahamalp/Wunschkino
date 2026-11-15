using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Wunschkino.Data;
using Wunschkino.Gaps;
using Wunschkino.Resources;
using Wunschkino.Services;

namespace Wunschkino.Library;

/// <summary>Stellt die Detailansicht eines Bibliothekstitels zusammen: Metadaten aus Jellyfin, bei Serien die Staffeln samt fehlender Folgen.</summary>
public sealed class LibraryDetailsService(
    LibraryCache library,
    SettingsService settings,
    JellyfinClient jellyfin,
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    ILogger<LibraryDetailsService> logger)
{
    /// <summary>Wer mehrmals denselben Titel öffnet, fragt Jellyfin nur einmal.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Auch Fehler werden kurz gemerkt: Bei gestörtem Jellyfin löst nicht jedes erneute Öffnen eine neue Anfrage aus.</summary>
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);

    // Dialog-Aufrufe laufen über die Blazor-Verbindung und damit am HTTP-Rate-Limit vorbei; die Grenze schützt Jellyfin vor Schleifen über viele Titel.
    private static readonly SemaphoreSlim JellyfinGate = new(2, 2);

    public async Task<LibraryDetailsResult> GetAsync(string jellyfinId, CancellationToken ct = default)
    {
        // Nur Titel aus dem aktuellen Bestand: Sonst ließe sich über die ID jeder Eintrag aus Jellyfin abfragen, auch Reste außerhalb der Bibliotheken.
        if (!library.TryGet(jellyfinId, out var item))
            return LibraryDetailsResult.Fail(Texts.Details_NotInLibrary);

        var key = $"library-details:{item.JellyfinId}";
        if (cache.TryGetValue(key, out LibraryDetailsResult? cached) && cached is not null)
            return cached;

        var url = await settings.GetAsync(SettingKeys.JellyfinUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.JellyfinApiKey, ct);
        if (url is null || apiKey is null)
            return LibraryDetailsResult.Fail(Texts.Msg_Library_NotConfigured);

        await JellyfinGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var result = await LoadAsync(item, url, apiKey, ct);
            cache.Set(key, result, result.Details is null ? FailureLifetime : CacheLifetime);
            return result;
        }
        finally
        {
            JellyfinGate.Release();
        }
    }

    private async Task<LibraryDetailsResult> LoadAsync(LibraryItem item, string url, string apiKey, CancellationToken ct)
    {
        try
        {
            var info = await jellyfin.GetDetailsAsync(url, apiKey, item.JellyfinId, ct);
            if (info is null)
                return LibraryDetailsResult.Fail(Texts.Details_NotInLibrary);

            var details = item.Kind == MediaKind.Series
                ? await WithSeasonsAsync(info, url, apiKey, item.JellyfinId, ct)
                : new LibraryDetails(info, [], MissingIsUncertain: false);
            return LibraryDetailsResult.Ok(details);
        }
        catch (JellyfinException ex)
        {
            logger.LogWarning("Details zu {JellyfinId} konnten nicht geladen werden: {Reason}", item.JellyfinId, ex.Message);
            return LibraryDetailsResult.Fail(ex.Message);
        }
    }

    private async Task<LibraryDetails> WithSeasonsAsync(JellyfinDetails info, string url, string apiKey, string jellyfinId, CancellationToken ct)
    {
        var episodes = await jellyfin.GetEpisodesAsync(url, apiKey, jellyfinId, ct);
        var gap = await FindGapAsync(jellyfinId, ct);
        var seasons = SeasonList.Build(episodes, gap);
        var uncertain = gap?.Status == GapStatus.UncertainNumbering && seasons.Any(s => s.Missing.Count > 0);
        return new LibraryDetails(info, seasons, uncertain);
    }

    // Die fehlenden Folgen sind nur eine Ergänzung: Klemmt die Datenbank, zeigt der Dialog die Staffeln trotzdem.
    private async Task<SeriesGap?> FindGapAsync(string jellyfinId, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.SeriesGaps.AsNoTracking().FirstOrDefaultAsync(g => g.JellyfinId == jellyfinId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fehlende Folgen zu {JellyfinId} konnten nicht gelesen werden", jellyfinId);
            return null;
        }
    }
}
