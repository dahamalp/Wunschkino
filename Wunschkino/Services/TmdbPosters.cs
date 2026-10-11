using System.Text.RegularExpressions;

namespace Wunschkino.Services;

public enum TmdbPosterSize
{
    Small,
    Medium,
    Large
}

public static partial class TmdbPosters
{
    /// <summary>
    /// TMDB-Posterpfade sehen aus wie „/abc123XYZ.jpg“. Die strenge Prüfung verhindert, dass über den Proxy
    /// beliebige Pfade auf image.tmdb.org (oder per „..“ darüber hinaus) abgefragt werden.
    /// </summary>
    public static bool IsValidPath(string? path) => path is not null && PathPattern().IsMatch(path);

    public static string SizeSegment(TmdbPosterSize size) => size switch
    {
        TmdbPosterSize.Small => "w185",
        TmdbPosterSize.Large => "w500",
        _ => "w342"
    };

    public static TmdbPosterSize ParseSize(int? width) => width switch
    {
        <= 185 => TmdbPosterSize.Small,
        >= 500 => TmdbPosterSize.Large,
        _ => TmdbPosterSize.Medium
    };

    /// <summary>Lokale Proxy-URL für einen gespeicherten Posterpfad, oder null ohne Poster.</summary>
    public static string? ProxyUrl(string? posterPath, int width = 342) =>
        IsValidPath(posterPath) ? $"/tmdb-posters{posterPath}?w={width}" : null;

    [GeneratedRegex(@"^/[A-Za-z0-9_-]{5,60}\.(jpg|png)\z")]
    private static partial Regex PathPattern();
}
