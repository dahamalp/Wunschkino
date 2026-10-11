using System.Text.Json;
using Wunschkino.Library;

namespace Wunschkino.Tests.Helpers;

public static class JellyfinFixtures
{
    public static string ItemsJson(int totalRecordCount, params object[] items) =>
        JsonSerializer.Serialize(new { Items = items, TotalRecordCount = totalRecordCount });

    public static object Movie(string id, string name, int? year = null, string? tmdb = null, string? imdb = null, bool poster = true) =>
        Item(id, name, "Movie", year, tmdb, imdb, poster);

    public static object Series(string id, string name, int? year = null, string? tmdb = null, string? imdb = null, bool poster = true) =>
        Item(id, name, "Series", year, tmdb, imdb, poster);

    public static object Item(string id, string name, string type, int? year = null, string? tmdb = null, string? imdb = null, bool poster = true) => new
    {
        Id = id,
        Name = name,
        Type = type,
        ProductionYear = year,
        DateCreated = "2026-09-01T10:00:00.0000000Z",
        ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = tmdb, ["Imdb"] = imdb },
        ImageTags = poster ? new Dictionary<string, string> { ["Primary"] = "tag" } : new Dictionary<string, string>()
    };

    public static LibraryItem LibraryItem(string id, string title, MediaKind kind = MediaKind.Movie, int? year = null,
        string? tmdb = null, string? imdb = null, DateTime? added = null)
    {
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tmdb is not null) providers["Tmdb"] = tmdb;
        if (imdb is not null) providers["Imdb"] = imdb;
        return new LibraryItem(id, title, kind, year, added, HasPoster: true, providers);
    }
}
