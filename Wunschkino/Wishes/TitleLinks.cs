using System.Text.RegularExpressions;
using Wunschkino.Library;

namespace Wunschkino.Wishes;

/// <summary>Adressen der Titelseiten bei TMDB und IMDb, damit ein Admin den richtigen Film oder die richtige Serie sofort findet.</summary>
public static partial class TitleLinks
{
    public static string Tmdb(MediaKind kind, int tmdbId) =>
        $"https://www.themoviedb.org/{(kind == MediaKind.Movie ? "movie" : "tv")}/{tmdbId}";

    /// <summary>Null, wenn die Nummer keine gültige IMDb-Nummer ist: Sie landet in einem Link und darf nur aus „tt“ und Ziffern bestehen.</summary>
    public static string? Imdb(string? imdbId) =>
        imdbId is not null && ImdbIdPattern().IsMatch(imdbId) ? $"https://www.imdb.com/title/{imdbId}/" : null;

    [GeneratedRegex(@"^tt\d{1,12}$")]
    private static partial Regex ImdbIdPattern();
}
