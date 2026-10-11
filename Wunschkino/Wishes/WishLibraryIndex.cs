using System.Globalization;
using Wunschkino.Anime;
using Wunschkino.Library;

namespace Wunschkino.Wishes;

/// <summary>
/// Schlüssel des Jellyfin-Bestands für den Abgleich mit Wünschen. Neben TMDB- und IMDb-ID zählt bei Anime auch die
/// AniList-ID eines Jellyfin-Plugins: Sie wird über die Zuordnungsliste in TMDB-/IMDb-IDs übersetzt, weil ein Wunsch nur diese kennt.
/// </summary>
public sealed class WishLibraryIndex
{
    private readonly HashSet<(MediaKind Kind, int TmdbId)> _tmdb = [];
    private readonly HashSet<string> _imdb = new(StringComparer.OrdinalIgnoreCase);

    public static WishLibraryIndex Build(IEnumerable<LibraryItem> items, AnimeIdMapping animeMapping)
    {
        var index = new WishLibraryIndex();
        foreach (var item in items)
        {
            if (TryParseId(item.TmdbId, out var tmdbId))
                index._tmdb.Add((item.Kind, tmdbId));
            if (item.ImdbId is { } imdbId)
                index._imdb.Add(imdbId);

            if (TryParseId(item.AniListId, out var aniListId) && animeMapping.Find(aniListId) is { } mapping)
            {
                if (mapping.HasTmdb)
                    index._tmdb.Add((mapping.Kind!.Value, mapping.TmdbId!.Value));
                foreach (var id in mapping.ImdbIds)
                    index._imdb.Add(id);
            }
        }
        return index;
    }

    /// <remarks>TMDB-IDs sind nur je Typ eindeutig: Film 1399 und Serie 1399 sind verschiedene Titel.</remarks>
    public bool Contains(MediaKind kind, int tmdbId, string? imdbId) =>
        _tmdb.Contains((kind, tmdbId)) || (imdbId is not null && _imdb.Contains(imdbId));

    private static bool TryParseId(string? text, out int id) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
}
