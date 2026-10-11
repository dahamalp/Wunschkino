using System.Text.Json;
using System.Text.RegularExpressions;
using Wunschkino.Library;

namespace Wunschkino.Anime;

/// <param name="Kind">Film oder Serie bei TMDB; null, wenn die Liste keine TMDB-ID kennt.</param>
public sealed record AnimeMapping(int AniListId, MediaKind? Kind, int? TmdbId, IReadOnlyList<string> ImdbIds)
{
    public bool HasTmdb => Kind is not null && TmdbId is not null;
}

/// <summary>
/// Ordnet AniList-IDs den TMDB-/IMDb-IDs aus der Community-Liste (Fribb/anime-lists) zu.
/// Jeder Stand ist unveränderlich und wird als Ganzes ausgetauscht.
/// </summary>
public sealed partial class AnimeIdMapping
{
    private readonly object _writeGate = new();
    private State _state = State.Empty;

    public int Count => Volatile.Read(ref _state).Entries.Count;

    /// <summary>Zeitpunkt der letzten erfolgreichen Prüfung (Download oder „unverändert“).</summary>
    public DateTime? CheckedUtc => Volatile.Read(ref _state).CheckedUtc;

    public AnimeMapping? Find(int aniListId) => Volatile.Read(ref _state).Entries.GetValueOrDefault(aniListId);

    /// <summary>Ob die Serie mit dieser TMDB-ID in der Zuordnungsliste als Anime geführt wird.</summary>
    public bool IsAnimeSeries(int tmdbId) => Volatile.Read(ref _state).SeriesTmdbIds.Contains(tmdbId);

    public void Replace(IReadOnlyDictionary<int, AnimeMapping> entries, DateTime checkedUtc)
    {
        lock (_writeGate)
            Volatile.Write(ref _state, new State(entries, checkedUtc));
    }

    public void MarkChecked(DateTime checkedUtc)
    {
        // Lesen und Schreiben gehören zusammen, sonst könnte ein paralleles Replace überschrieben werden.
        lock (_writeGate)
            Volatile.Write(ref _state, Volatile.Read(ref _state) with { CheckedUtc = checkedUtc });
    }

    /// <summary>
    /// Liest die Liste. Einträge ohne AniList-ID und solche ohne jede nutzbare TMDB-/IMDb-ID werden verworfen,
    /// ebenso unerwartete Feldformen – eine einzelne kaputte Zeile soll nicht die ganze Liste unbrauchbar machen.
    /// </summary>
    /// <exception cref="JsonException">Wenn die Datei kein JSON-Array ist.</exception>
    public static IReadOnlyDictionary<int, AnimeMapping> Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Die Anime-Liste hat ein unerwartetes Format.");

        var entries = new Dictionary<int, AnimeMapping>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("anilist_id", out var idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt32(out var aniListId)
                || !IsPlausibleId(aniListId))
                continue;

            var isMovie = element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "MOVIE";
            var (kind, tmdbId) = ReadTmdb(element, isMovie);
            var imdbIds = ReadImdbIds(element);
            if (kind is null && imdbIds.Count == 0)
                continue;

            entries.TryAdd(aniListId, new AnimeMapping(aniListId, kind, tmdbId, imdbIds));
        }
        return entries;
    }

    private static (MediaKind? Kind, int? TmdbId) ReadTmdb(JsonElement element, bool isMovie)
    {
        if (!element.TryGetProperty("themoviedb_id", out var tmdb) || tmdb.ValueKind != JsonValueKind.Object)
            return (null, null);

        int? tv = tmdb.TryGetProperty("tv", out var tvElement) && tvElement.ValueKind == JsonValueKind.Number && tvElement.TryGetInt32(out var tvId) && IsPlausibleId(tvId) ? tvId : null;
        int? movie = null;
        if (tmdb.TryGetProperty("movie", out var movies) && movies.ValueKind == JsonValueKind.Array)
        {
            // Mehrere Film-IDs gibt es bei Zusammenschnitten; die erste ist die Hauptzuordnung.
            foreach (var candidate in movies.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.Number && candidate.TryGetInt32(out var movieId) && IsPlausibleId(movieId))
                {
                    movie = movieId;
                    break;
                }
            }
        }

        if (movie is not null && (tv is null || isMovie))
            return (MediaKind.Movie, movie);
        return tv is not null ? (MediaKind.Series, tv) : (null, null);
    }

    private static List<string> ReadImdbIds(JsonElement element)
    {
        var result = new List<string>();
        if (element.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in imdb.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { } text && ImdbPattern().IsMatch(text))
                    result.Add(text);
            }
        }
        return result;
    }

    // AniList- und TMDB-IDs liegen heute im sechs- bis siebenstelligen Bereich; viel größere Werte sind Ausreißer.
    public const int MaxPlausibleId = 100_000_000;

    private static bool IsPlausibleId(int id) => id is > 0 and <= MaxPlausibleId;

    [GeneratedRegex(@"^tt\d{7,10}\z")]
    private static partial Regex ImdbPattern();

    private sealed record State(IReadOnlyDictionary<int, AnimeMapping> Entries, DateTime? CheckedUtc)
    {
        public static readonly State Empty = new(new Dictionary<int, AnimeMapping>(), null);

        // Die Einträge ändern sich pro Stand nie; daher darf die Menge beim ersten Zugriff einmal gebaut werden.
        private readonly Lazy<HashSet<int>> _seriesTmdbIds = new(() => Entries.Values
            .Where(mapping => mapping.Kind == MediaKind.Series && mapping.TmdbId is not null)
            .Select(mapping => mapping.TmdbId!.Value)
            .ToHashSet());

        public HashSet<int> SeriesTmdbIds => _seriesTmdbIds.Value;
    }
}
