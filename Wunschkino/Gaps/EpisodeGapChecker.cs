namespace Wunschkino.Gaps;

/// <param name="AirDate">Sendedatum laut TMDB; null, wenn TMDB noch keines kennt.</param>
public sealed record TmdbEpisode(int Season, int Number, DateOnly? AirDate);

/// <param name="NumberEnd">Letzte Folge einer Doppelfolgen-Datei (z. B. 2 bis 3); sonst null.</param>
public sealed record JellyfinEpisode(int Season, int Number, int? NumberEnd);

public enum GapStatus
{
    Complete,
    Missing,

    /// <summary>Es fehlen scheinbar Folgen, aber die Zählung von Jellyfin und TMDB passt nicht sicher zusammen (typisch bei Anime).</summary>
    UncertainNumbering
}

public sealed record SeasonGap(int Season, IReadOnlyList<int> MissingEpisodes, bool WholeSeasonMissing);

/// <param name="ExpectedCount">Zahl der schon ausgestrahlten Folgen laut TMDB (ohne Specials, ohne Karenzzeit-Folgen).</param>
/// <param name="PresentCount">Davon in Jellyfin vorhanden.</param>
public sealed record GapResult(GapStatus Status, IReadOnlyList<SeasonGap> Seasons, int ExpectedCount, int PresentCount);

/// <summary>Vergleicht die Folgen einer Serie in Jellyfin mit der Soll-Liste von TMDB. Reine Funktion, ohne Netz und Datenbank.</summary>
public static class EpisodeGapChecker
{
    /// <summary>Tage nach der Ausstrahlung, bevor eine Folge als fehlend gilt: Sie braucht Zeit, bis sie in Jellyfin liegt.</summary>
    public const int GraceDays = 2;

    // Schützt vor absurden Doppelfolgen-Angaben (etwa 1 bis 2 Milliarden), die sonst endlos Speicher belegen würden.
    private const int MaxEpisodesPerFile = 50;

    // Keine reale Serie hat solche Folgennummern; größere Werte sind Müll und würden beim Hochzählen überlaufen.
    private const int MaxEpisodeNumber = 100_000;

    public static GapResult Compare(
        IReadOnlyCollection<TmdbEpisode> expected,
        IReadOnlyCollection<JellyfinEpisode> present,
        DateOnly today,
        bool numberingMayDiffer)
    {
        var cutoff = today.AddDays(-GraceDays);
        var aired = expected
            .Where(e => e.Season > 0 && e.AirDate is { } date && date <= cutoff)
            .Select(e => (e.Season, e.Number))
            .Distinct()
            .ToList();
        var known = expected.Where(e => e.Season > 0).Select(e => (e.Season, e.Number)).ToHashSet();

        var have = ExpandPresent(present);
        var mismatch = have.Any(episode => !known.Contains(episode));

        var exact = MissingBySeason(aired, have.Contains);
        var presentCount = aired.Count(have.Contains);
        if (exact.Count == 0)
            return new GapResult(GapStatus.Complete, exact, aired.Count, presentCount);

        if (!mismatch && !numberingMayDiffer)
            return new GapResult(GapStatus.Missing, exact, aired.Count, presentCount);

        return CompareWithUncertainNumbering(known, aired, have, exact, presentCount, mismatch);
    }

    /// <summary>
    /// Anime wie Naruto: TMDB teilt sie in viele Staffeln, zählt die Folgen aber durchgehend weiter, Jellyfin führt sie
    /// meist als eine Staffel oder beginnt je Staffel wieder bei 1. Reihenfolge der Stufen: Nummernvergleich, wenn die Nummern
    /// bei TMDB und in Jellyfin über alle Staffeln eindeutig sind; die genaue Liste, wenn jede Jellyfin-Folge zu TMDB passt;
    /// sonst nur die Anzahl.
    /// </summary>
    private static GapResult CompareWithUncertainNumbering(
        HashSet<(int Season, int Number)> known,
        List<(int Season, int Number)> aired,
        HashSet<(int Season, int Number)> have,
        List<SeasonGap> exact,
        int exactPresentCount,
        bool mismatch)
    {
        if (NumbersAreUnique(known) && NumbersAreUnique(have))
        {
            var haveNumbers = have.Select(episode => episode.Number).ToHashSet();
            var missingByNumber = MissingBySeason(aired, episode => haveNumbers.Contains(episode.Number));
            var presentByNumber = aired.Count(episode => haveNumbers.Contains(episode.Number));
            return missingByNumber.Count == 0
                ? new GapResult(GapStatus.Complete, missingByNumber, aired.Count, presentByNumber)
                : new GapResult(GapStatus.UncertainNumbering, missingByNumber, aired.Count, presentByNumber);
        }

        // Passt jede Jellyfin-Folge zu TMDB, stimmt die Staffelzählung: Dann ist die genaue Liste verlässlich genug.
        if (!mismatch)
            return new GapResult(GapStatus.UncertainNumbering, exact, aired.Count, exactPresentCount);

        return have.Count >= aired.Count
            ? new GapResult(GapStatus.Complete, [], aired.Count, aired.Count)
            : new GapResult(GapStatus.UncertainNumbering, [], aired.Count, have.Count);
    }

    private static bool NumbersAreUnique(HashSet<(int Season, int Number)> episodes) =>
        episodes.Select(episode => episode.Number).Distinct().Count() == episodes.Count;

    private static List<SeasonGap> MissingBySeason(
        List<(int Season, int Number)> aired,
        Func<(int Season, int Number), bool> isPresent) => aired
        .Where(episode => !isPresent(episode))
        .GroupBy(episode => episode.Season)
        .OrderBy(group => group.Key)
        .Select(group => new SeasonGap(
            group.Key,
            group.Select(episode => episode.Number).Order().ToList(),
            WholeSeasonMissing: !aired.Any(episode => episode.Season == group.Key && isPresent(episode))))
        .ToList();

    private static HashSet<(int Season, int Number)> ExpandPresent(IReadOnlyCollection<JellyfinEpisode> present)
    {
        var have = new HashSet<(int, int)>();
        foreach (var episode in present.Where(e => e.Season > 0 && e.Number is > 0 and <= MaxEpisodeNumber))
        {
            var count = (int)Math.Clamp((long)(episode.NumberEnd ?? episode.Number) - episode.Number + 1, 1, MaxEpisodesPerFile);
            for (var offset = 0; offset < count; offset++)
                have.Add((episode.Season, episode.Number + offset));
        }
        return have;
    }
}
