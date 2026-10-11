using Microsoft.EntityFrameworkCore;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Library;

namespace Wunschkino.Wishes;

/// <summary>Setzt Wünsche auf „Erledigt“, sobald ihr Titel nach einem Bestands-Refresh in Jellyfin auftaucht.</summary>
public sealed class WishAutoCompleter(
    IDbContextFactory<AppDbContext> dbFactory,
    AnimeIdMapping animeMapping,
    TimeProvider time,
    ILogger<WishAutoCompleter> logger)
{
    // Hält die Zahl der SQL-Parameter je Anweisung weit unter dem SQLite-Limit.
    private const int BatchSize = 500;

    /// <param name="items">Der frisch geladene, erfolgreiche Bestand. Ein leerer Bestand ändert nichts – er deutet eher auf einen Fehler als auf eine leere Bibliothek.</param>
    /// <returns>Die Zahl der Wünsche, die dabei auf „Erledigt“ gesetzt wurden.</returns>
    public async Task<int> CompleteAsync(IReadOnlyList<LibraryItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
            return 0;

        var index = WishLibraryIndex.Build(items, animeMapping);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = await db.Wishes.AsNoTracking()
            .Where(w => (w.Status == WishStatus.Open || w.Status == WishStatus.Accepted) && w.AutoCompletion != WishAutoCompletion.Suppressed)
            .Select(w => new { w.Id, w.TmdbId, w.Kind, w.ImdbId })
            .ToListAsync(ct);
        var matchedIds = open.Where(w => index.Contains(w.Kind, w.TmdbId, w.ImdbId)).Select(w => w.Id).ToList();
        if (matchedIds.Count == 0)
            return 0;

        var now = time.GetUtcNow().UtcDateTime;
        var completed = 0;
        foreach (var batch in matchedIds.Chunk(BatchSize))
        {
            // Die Bedingungen stehen noch einmal im Update: Hat ein Admin den Wunsch in der Zwischenzeit abgelehnt
            // oder erledigt, bleibt dessen Entscheidung stehen.
            completed += await db.Wishes
                .Where(w => batch.Contains(w.Id)
                            && (w.Status == WishStatus.Open || w.Status == WishStatus.Accepted)
                            && w.AutoCompletion != WishAutoCompletion.Suppressed)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(w => w.Status, WishStatus.Done)
                    .SetProperty(w => w.AutoCompletion, WishAutoCompletion.Completed)
                    .SetProperty(w => w.UpdatedUtc, now), ct);
        }

        if (completed > 0)
            logger.LogInformation("{Count} Wünsche automatisch auf „Erledigt“ gesetzt, weil sie jetzt in Jellyfin liegen.", completed);
        return completed;
    }
}
