using Wunschkino.Data;
using Wunschkino.Gaps;

namespace Wunschkino.Library;

/// <summary>Ordnet die Folgen einer Serie nach Staffeln und ergänzt die fehlenden aus der letzten Folgen-Prüfung.</summary>
public static class SeasonList
{
    // Wie in EpisodeGapChecker: Größere Nummern sind Müll und würden beim Hochzählen überlaufen.
    private const int MaxEpisodeNumber = 100_000;
    private const int MaxEpisodesPerFile = 50;

    public static IReadOnlyList<SeasonOverview> Build(IReadOnlyCollection<JellyfinEpisode> present, SeriesGap? gap)
    {
        var episodes = present
            .Where(e => e.Season >= 0 && e.Number is >= 0 and <= MaxEpisodeNumber)
            .GroupBy(e => e.Season)
            .ToDictionary(
                g => g.Key,
                g => g.DistinctBy(e => e.Number).OrderBy(e => e.Number).Select(e => new EpisodeEntry(e.Number, e.NumberEnd, e.Title)).ToList());

        var missing = MissingBySeason(gap);
        return episodes.Keys.Union(missing.Keys)
            .OrderBy(season => season == 0 ? int.MaxValue : season)
            .Select(season =>
            {
                var entries = episodes.GetValueOrDefault(season) ?? [];
                // Die Prüfung ist bis zu drei Tage alt: Was inzwischen in Jellyfin liegt, fehlt nicht mehr.
                var covered = entries.SelectMany(Covers).ToHashSet();
                var stillMissing = (missing.GetValueOrDefault(season) ?? []).Where(n => !covered.Contains(n)).Distinct().Order().ToList();
                return new SeasonOverview(season, entries, stillMissing);
            })
            .Where(season => season.Episodes.Count > 0 || season.Missing.Count > 0)
            .ToList();
    }

    private static IEnumerable<int> Covers(EpisodeEntry entry) =>
        Enumerable.Range(entry.Number, (int)Math.Clamp((long)(entry.NumberEnd ?? entry.Number) - entry.Number + 1, 1, MaxEpisodesPerFile));

    private static Dictionary<int, IReadOnlyList<int>> MissingBySeason(SeriesGap? gap) =>
        gap is { HasResult: true } && gap.Status != GapStatus.Complete
            ? SeriesGapSerializer.Deserialize(gap.MissingJson).GroupBy(s => s.Season)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.SelectMany(s => s.MissingEpisodes).ToList())
            : [];
}
