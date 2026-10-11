namespace Wunschkino.Library;

public enum MediaKind
{
    Movie,
    Series
}

/// <param name="ProviderIds">Externe IDs aus Jellyfin, z. B. „Imdb“, „Tmdb“, „Tvdb“ oder „AniList“ (Groß-/Kleinschreibung egal).</param>
public sealed record LibraryItem(
    string JellyfinId,
    string Title,
    MediaKind Kind,
    int? Year,
    DateTime? AddedUtc,
    bool HasPoster,
    IReadOnlyDictionary<string, string> ProviderIds)
{
    public string? ImdbId => ProviderIds.GetValueOrDefault("Imdb");
    public string? TmdbId => ProviderIds.GetValueOrDefault("Tmdb");
    public string? AniListId => ProviderIds.GetValueOrDefault("AniList");
}
