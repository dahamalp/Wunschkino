using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Services;

namespace Wunschkino.Gaps;

/// <summary>
/// Prüft Serien aus Jellyfin auf fehlende Folgen und hält das Ergebnis je Serie fest. Jede Serie wird alle <see cref="Interval"/> neu
/// geprüft, davon pro Lauf nur ein Teil, damit Jellyfin und TMDB in der laufenden Pflege nicht belastet werden. Nie geprüfte Serien
/// (erstes Befüllen) und ein vom Admin angeforderter Neu-Scan laufen dagegen in einem Zug: Das ist einmalige Last mit Absicht.
/// </summary>
public sealed class SeriesGapService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    JellyfinClient jellyfin,
    TmdbClient tmdb,
    AnimeIdMapping animeMapping,
    SeriesGapProgress progress,
    TimeProvider time,
    ILogger<SeriesGapService> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(3);

    /// <summary>Nach einem Fehlschlag ist die Serie früher wieder dran als nach einem Erfolg.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    // Begrenzt in der laufenden Pflege, wie viele Serien mit abgelaufener Frist ein Lauf prüft; der Rest folgt in den nächsten Läufen.
    private const int MaxPerRun = 20;

    // Mehrere Fehler in Folge deuten auf abgelehnten Schlüssel, Limit oder Ausfall hin; dann bringen weitere Anfragen nichts.
    private const int MaxFailuresInARow = 3;

    /// <param name="library">Der frisch geladene, erfolgreiche Bestand. Ein leerer Bestand ändert nichts – er deutet eher auf einen Fehler als auf eine leere Bibliothek.</param>
    /// <returns>Die Zahl der Serien, die dabei erfolgreich geprüft wurden.</returns>
    public async Task<int> CheckDueAsync(IReadOnlyList<LibraryItem> library, CancellationToken ct = default)
    {
        var fullScan = progress.BeginRun();
        try
        {
            return await CheckAsync(library, fullScan, ct);
        }
        finally
        {
            progress.Finish();
        }
    }

    private async Task<int> CheckAsync(IReadOnlyList<LibraryItem> library, bool fullScan, CancellationToken ct)
    {
        if (library.Count == 0)
            return 0;

        var jellyfinUrl = await settings.GetAsync(SettingKeys.JellyfinUrl, ct);
        var jellyfinKey = await settings.GetAsync(SettingKeys.JellyfinApiKey, ct);
        var tmdbKey = await settings.GetAsync(SettingKeys.TmdbApiKey, ct);
        if (jellyfinUrl is null || jellyfinKey is null || tmdbKey is null)
            return 0;

        var series = library
            .Where(item => item.Kind == MediaKind.Series && TryParseId(item.TmdbId, out _))
            .ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SeriesGaps.ToDictionaryAsync(row => row.JellyfinId, ct);

        var inLibrary = series.Select(item => item.JellyfinId).ToHashSet();
        var gone = rows.Values.Where(row => !inLibrary.Contains(row.JellyfinId)).ToList();
        if (gone.Count > 0)
        {
            db.SeriesGaps.RemoveRange(gone);
            await db.SaveChangesAsync(ct);
            foreach (var row in gone)
                rows.Remove(row.JellyfinId);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var entries = series
            .Select(item => (Item: item, Row: rows.GetValueOrDefault(item.JellyfinId)))
            .ToList();

        // Neu-Scan: jede Serie, die ältesten zuerst. Sonst alle nie geprüften auf einmal (erstes Befüllen) plus, begrenzt, die mit abgelaufener Frist.
        var due = fullScan
            ? entries.OrderBy(entry => entry.Row?.LastAttemptUtc ?? DateTime.MinValue).ToList()
            : entries.Where(entry => entry.Row is null)
                .Concat(entries
                    .Where(entry => entry.Row is not null && entry.Row.LastAttemptUtc <= now - Interval)
                    .OrderBy(entry => entry.Row!.LastAttemptUtc)
                    .Take(MaxPerRun))
                .ToList();
        progress.SetTotal(due.Count);

        var checkedCount = 0;
        var failuresInARow = 0;
        foreach (var (item, existing) in due)
        {
            TryParseId(item.TmdbId, out var tmdbId);
            var row = existing ?? new SeriesGap { JellyfinId = item.JellyfinId, Title = item.Title, TmdbId = tmdbId };
            row.Title = item.Title;
            row.TmdbId = tmdbId;

            GapResult? result;
            try
            {
                var present = await jellyfin.GetEpisodesAsync(jellyfinUrl, jellyfinKey, item.JellyfinId, ct);
                var expected = await tmdb.GetEpisodesAsync(tmdbKey, tmdbId, ct);
                var numberingMayDiffer = item.AniListId is not null || animeMapping.IsAnimeSeries(tmdbId);
                result = expected is null
                    ? new GapResult(GapStatus.Complete, [], 0, 0)
                    : EpisodeGapChecker.Compare(expected, present, DateOnly.FromDateTime(now), numberingMayDiffer);
            }
            catch (Exception ex) when (ex is JellyfinException or TmdbException)
            {
                logger.LogWarning("Folgen von „{Title}“ konnten nicht geprüft werden: {Reason}", item.Title, ex.Message);
                row.LastAttemptUtc = now - Interval + RetryAfterFailure;
                await TrySaveAsync(db, row, ct);
                progress.Advance();
                if (++failuresInARow >= MaxFailuresInARow)
                    break;
                continue;
            }

            failuresInARow = 0;
            row.HasResult = true;
            row.Status = result.Status;
            row.ExpectedCount = result.ExpectedCount;
            row.PresentCount = result.PresentCount;
            row.MissingJson = SeriesGapSerializer.Serialize(result.Seasons);
            row.LastAttemptUtc = now;
            if (await TrySaveAsync(db, row, ct))
                checkedCount++;
            progress.Advance();
        }

        if (checkedCount > 0)
            logger.LogInformation("Folgen von {Count} Serien geprüft.", checkedCount);
        return checkedCount;
    }

    /// <summary>
    /// Speichert eine Zeile; neue Zeilen werden erst hier angemeldet, damit eine halb gefüllte nie im Context hängen bleibt.
    /// Ein Konflikt (etwa derselbe Jellyfin-Eintrag zweimal oder ein paralleler Lauf) betrifft nur diese Serie, nicht den ganzen Lauf.
    /// </summary>
    private async Task<bool> TrySaveAsync(AppDbContext db, SeriesGap row, CancellationToken ct)
    {
        if (row.Id == 0 && db.Entry(row).State == EntityState.Detached)
            db.SeriesGaps.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Das Ergebnis für „{Title}“ konnte nicht gespeichert werden.", row.Title);
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    private static bool TryParseId(string? text, out int id) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
