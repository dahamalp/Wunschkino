using System.Text.Json;

namespace Wunschkino.Gaps;

/// <summary>Wandelt die fehlenden Folgen einer Serie in das JSON um, das in <c>SeriesGap.MissingJson</c> liegt, und zurück.</summary>
public static class SeriesGapSerializer
{
    public static string Serialize(IReadOnlyList<SeasonGap> seasons) => JsonSerializer.Serialize(seasons);

    /// <returns>Die Staffeln mit fehlenden Folgen; bei leerem oder unlesbarem JSON eine leere Liste.</returns>
    public static IReadOnlyList<SeasonGap> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            // Ein Eintrag ohne Folgenliste (etwa „[{}]“) ließe sich nicht darstellen und wird verworfen.
            return (JsonSerializer.Deserialize<List<SeasonGap?>>(json) ?? [])
                .Where(season => season?.MissingEpisodes is not null)
                .Select(season => season!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
