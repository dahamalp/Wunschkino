using Wunschkino.Gaps;

namespace Wunschkino.Data;

/// <summary>
/// Ergebnis der letzten Folgen-Prüfung einer Serie aus Jellyfin. Gespeichert wird nur das berechnete Ergebnis, keine TMDB-Rohdaten
/// (Folgentitel, Beschreibungen): Das hält die TMDB-Regel zum Zwischenspeichern ein.
/// </summary>
public class SeriesGap
{
    public int Id { get; set; }
    public required string JellyfinId { get; set; }
    public int TmdbId { get; set; }
    public required string Title { get; set; }

    /// <summary>false, solange noch keine Prüfung gelungen ist (nur Fehlversuche).</summary>
    public bool HasResult { get; set; }

    public GapStatus Status { get; set; }
    public int ExpectedCount { get; set; }
    public int PresentCount { get; set; }

    /// <summary>Fehlende Folgen je Staffel als JSON (<see cref="SeasonGap"/>-Liste).</summary>
    public string? MissingJson { get; set; }

    /// <summary>Letzter Versuch, auch wenn er scheiterte; bestimmt, wann die Serie wieder an der Reihe ist.</summary>
    public DateTime LastAttemptUtc { get; set; }
}
