namespace Wunschkino.Gaps;

/// <summary>Macht aus den fehlenden Folgen einer Staffel einen lesbaren Satz („Staffel 3: Folgen 3, 5–6, 9“).</summary>
public static class MissingEpisodesText
{
    public static IReadOnlyList<string> DescribeAll(IEnumerable<SeasonGap> seasons) =>
        seasons.OrderBy(season => season.Season).Select(Describe).ToList();

    public static string Describe(SeasonGap gap)
    {
        var count = gap.MissingEpisodes.Count;
        if (gap.WholeSeasonMissing)
            return $"Staffel {gap.Season} komplett ({count} {(count == 1 ? "Folge" : "Folgen")})";

        return $"Staffel {gap.Season}: {(count == 1 ? "Folge" : "Folgen")} {Runs(gap.MissingEpisodes)}";
    }

    private static string Runs(IEnumerable<int> numbers)
    {
        var parts = new List<string>();
        var sorted = numbers.Distinct().Order().ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            var end = start;
            while (i + 1 < sorted.Count && sorted[i + 1] == end + 1)
                end = sorted[++i];

            parts.Add(end == start ? start.ToString() : $"{start}–{end}");
        }
        return string.Join(", ", parts);
    }
}
