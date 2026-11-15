namespace Wunschkino.Library;

public sealed record CastMember(string Name, string? Role);

/// <param name="Runtime">Nur bei Filmen sinnvoll; bei Serien liefert Jellyfin die Länge einer Folge.</param>
/// <param name="EndYear">Letztes Jahr einer beendeten Serie; null bei laufenden Serien und Filmen.</param>
public sealed record JellyfinDetails(
    string? Overview,
    string? Tagline,
    IReadOnlyList<string> Genres,
    string? OfficialRating,
    double? CommunityRating,
    TimeSpan? Runtime,
    int? EndYear,
    bool IsContinuing,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> Directors,
    IReadOnlyList<CastMember> Cast);

/// <param name="NumberEnd">Letzte Folge einer Doppelfolgen-Datei (z. B. 1 bis 2); sonst null.</param>
public sealed record EpisodeEntry(int Number, int? NumberEnd, string? Title);

/// <param name="Missing">Fehlende Folgen laut letzter Prüfung auf fehlende Folgen; leer, wenn nichts fehlt oder nie geprüft wurde.</param>
public sealed record SeasonOverview(int Season, IReadOnlyList<EpisodeEntry> Episodes, IReadOnlyList<int> Missing)
{
    public bool IsSpecials => Season == 0;
}

/// <param name="MissingIsUncertain">Die Zählung von Jellyfin und TMDB passt nicht sicher zusammen (typisch bei Anime).</param>
public sealed record LibraryDetails(JellyfinDetails Info, IReadOnlyList<SeasonOverview> Seasons, bool MissingIsUncertain);

/// <summary>Entweder Details oder eine Meldung für den Nutzer.</summary>
public sealed record LibraryDetailsResult(LibraryDetails? Details, string? Error)
{
    public static LibraryDetailsResult Ok(LibraryDetails details) => new(details, null);
    public static LibraryDetailsResult Fail(string error) => new(null, error);
}
