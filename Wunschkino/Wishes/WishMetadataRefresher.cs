using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;
using Wunschkino.Localization;
using Wunschkino.Services;

namespace Wunschkino.Wishes;

/// <summary>
/// Holt die TMDB-Daten älterer Wünsche neu. Die TMDB-Bedingungen verbieten, TMDB-Daten länger als 6 Monate zu speichern;
/// die Frist ist mit Puffer gewählt, damit der Lauf sie auch nach einem Ausfall noch einhält.
/// </summary>
public sealed class WishMetadataRefresher(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    TmdbClient tmdb,
    TimeProvider time,
    ILogger<WishMetadataRefresher> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(150);

    // Begrenzt die Last auf TMDB je Lauf; der Rest folgt im nächsten Lauf.
    private const int MaxPerRun = 25;
    private const int MaxFailuresInARow = 3;

    /// <returns>Die Zahl der Wünsche, deren Daten dabei erneuert wurden.</returns>
    public async Task<int> RefreshStaleAsync(CancellationToken ct = default)
    {
        var apiKey = await settings.GetAsync(SettingKeys.TmdbApiKey, ct);
        if (apiKey is null)
            return 0;
        // Im Hintergrund gibt es keine Anfrage und damit keine Sprache des Nutzers: Die Titel kommen in der Standardsprache.
        var language = AppLanguages.Normalize(await settings.GetAsync(SettingKeys.DefaultLanguage, ct)) ?? AppLanguages.Default;
        var tmdbLanguage = AppLanguages.TmdbCode(CultureInfo.GetCultureInfo(language));

        var now = time.GetUtcNow().UtcDateTime;
        var threshold = now - MaxAge;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stale = await db.Wishes
            .Where(w => w.MetadataFetchedUtc < threshold)
            .OrderBy(w => w.MetadataFetchedUtc)
            .Take(MaxPerRun)
            .ToListAsync(ct);

        var refreshed = 0;
        var failuresInARow = 0;
        foreach (var wish in stale)
        {
            TmdbTitle? details;
            try
            {
                details = await tmdb.GetDetailsAsync(apiKey, wish.TmdbId, wish.Kind, ct, tmdbLanguage);
                failuresInARow = 0;
            }
            catch (TmdbException ex)
            {
                // Ein einzelner Fehler überspringt nur diesen Wunsch, sonst würde er als ältester jeden Lauf blockieren.
                // Mehrere in Folge deuten auf abgelehnten Schlüssel, Limit oder Ausfall hin: dann bringen weitere Anfragen nichts.
                logger.LogWarning("TMDB-Daten von Wunsch {WishId} konnten nicht aufgefrischt werden: {Reason}", wish.Id, ex.Message);
                if (++failuresInARow >= MaxFailuresInARow)
                    break;
                continue;
            }

            if (details is null)
            {
                // Der Titel ist bei TMDB verschwunden: Der Wunsch bleibt mit seinen Daten bestehen und wird erst nach der nächsten Frist wieder geprüft.
                logger.LogWarning("Wunsch {WishId} (TMDB {TmdbId}) existiert bei TMDB nicht mehr.", wish.Id, wish.TmdbId);
            }
            else
            {
                wish.Title = details.Title;
                wish.Year = details.Year;
                wish.PosterPath = details.PosterPath;
                wish.ImdbId = details.ImdbId ?? wish.ImdbId;
                refreshed++;
            }
            wish.MetadataFetchedUtc = now;
            await db.SaveChangesAsync(ct);
        }

        if (refreshed > 0)
            logger.LogInformation("TMDB-Daten von {Count} Wünschen aufgefrischt.", refreshed);
        return refreshed;
    }
}
