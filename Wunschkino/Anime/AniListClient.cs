using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Wunschkino.Resources;

namespace Wunschkino.Anime;

public sealed class AniListException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="CoverPath">Pfad des Covers auf s4.anilist.co (nur das geprüfte Format) oder null.</param>
public sealed record AniListAnime(int Id, string Romaji, string? English, string? Native, string? Format, int? Year, string? CoverPath)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(English) ? Romaji : English;
}

/// <param name="Duration">So lange muss die Anfrage warten.</param>
/// <param name="BecauseOfThisUser">Das Limit dieses Nutzers greift (nicht die gemeinsame Obergrenze).</param>
public readonly record struct AniListWait(TimeSpan Duration, bool BecauseOfThisUser);

/// <summary>
/// Begrenzt die Anfragen an AniList (dort gelten derzeit 30 pro Minute, normal 90), damit die App nie selbst in die Sperre läuft.
/// Je Nutzer gilt ein eigenes Limit, damit einer nicht allen anderen die Suche sperrt; die gemeinsame Obergrenze bleibt, weil AniList pro IP zählt.
/// </summary>
public sealed class AniListThrottle(TimeProvider time)
{
    public const int MaxRequestsPerMinute = 25;
    public const int MaxRequestsPerUserPerMinute = 8;

    // Ab dieser Zahl bekannter Nutzer werden abgelaufene Zähler aufgeräumt, damit der Speicher nicht unbegrenzt wächst.
    private const int SweepThreshold = 100;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly Dictionary<int, Queue<DateTimeOffset>> _requestsByUser = new();
    private readonly object _gate = new();
    private DateTimeOffset _blockedUntil;

    /// <summary>Zahl der Nutzer, für die gerade ein Zähler im Speicher liegt (für Tests des Aufräumens).</summary>
    public int TrackedUsers
    {
        get
        {
            lock (_gate)
                return _requestsByUser.Count;
        }
    }

    /// <summary>Hält alle Anfragen zurück, bis AniList selbst wieder Anfragen zulässt (nach einer 429-Antwort).</summary>
    public void BlockFor(TimeSpan duration)
    {
        var until = time.GetUtcNow() + duration;
        lock (_gate)
        {
            if (until > _blockedUntil)
                _blockedUntil = until;
        }
    }

    /// <returns>null, wenn die Anfrage erlaubt ist (und gezählt wird), sonst die Wartezeit.</returns>
    public AniListWait? TryAcquire(int userId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (now < _blockedUntil)
                return new AniListWait(_blockedUntil - now, false);

            DropExpired(_requests, now);
            if (!_requestsByUser.TryGetValue(userId, out var own))
            {
                if (_requestsByUser.Count >= SweepThreshold)
                    SweepExpiredUsers(now);
                own = _requestsByUser[userId] = new Queue<DateTimeOffset>();
            }
            DropExpired(own, now);

            if (own.Count >= MaxRequestsPerUserPerMinute)
                return new AniListWait(Window - (now - own.Peek()), true);
            if (_requests.Count >= MaxRequestsPerMinute)
                return new AniListWait(Window - (now - _requests.Peek()), false);

            own.Enqueue(now);
            _requests.Enqueue(now);
            return null;
        }
    }

    private static void DropExpired(Queue<DateTimeOffset> requests, DateTimeOffset now)
    {
        while (requests.Count > 0 && now - requests.Peek() >= Window)
            requests.Dequeue();
    }

    private void SweepExpiredUsers(DateTimeOffset now)
    {
        foreach (var userId in _requestsByUser.Keys.ToList())
        {
            DropExpired(_requestsByUser[userId], now);
            if (_requestsByUser[userId].Count == 0)
                _requestsByUser.Remove(userId);
        }
    }
}

public static partial class AniListCovers
{
    public const string Host = "s4.anilist.co";

    /// <summary>Nur Cover-Pfade im bekannten Format dürfen in die DB-freie Proxy-URL und an den Upstream.</summary>
    public static bool IsValidPath(string? path) => path is not null && PathPattern().IsMatch(path);

    public static string? TryGetPath(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
        && IsValidPath(uri.AbsolutePath)
            ? uri.AbsolutePath
            : null;

    public static string? ProxyUrl(string? coverPath) => IsValidPath(coverPath) ? $"/anilist-covers{coverPath}" : null;

    [GeneratedRegex(@"^/file/anilistcdn/media/anime/cover/[A-Za-z]{3,16}/[A-Za-z0-9_-][A-Za-z0-9._-]{2,99}\.(jpg|jpeg|png|webp)\z")]
    private static partial Regex PathPattern();
}

/// <summary>Anonyme Lesezugriffe auf die öffentliche AniList-GraphQL-Schnittstelle – kein Konto und kein Key nötig.</summary>
public sealed class AniListClient(HttpClient http, IMemoryCache cache, AniListThrottle throttle)
{
    public static readonly Uri ApiUrl = new("https://graphql.anilist.co/");
    public static readonly Uri CoverBaseUrl = new($"https://{AniListCovers.Host}");
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private const int MaxResults = 20;

    private const string SearchQuery = """
        query ($q: String, $perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(search: $q, type: ANIME, isAdult: false, sort: SEARCH_MATCH) {
              id
              format
              seasonYear
              startDate { year }
              title { romaji english native }
              coverImage { large }
            }
          }
        }
        """;

    /// <exception cref="AniListException">Wenn AniList nicht erreichbar ist, die Anfrage begrenzt oder Unlesbares liefert.</exception>
    public async Task<IReadOnlyList<AniListAnime>> SearchAsync(string query, int userId, CancellationToken ct = default)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var cacheKey = $"anilist:search:{normalized}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<AniListAnime>? cached) && cached is not null)
            return cached;

        if (throttle.TryAcquire(userId) is { } wait)
        {
            var seconds = Math.Ceiling(wait.Duration.TotalSeconds);
            throw new AniListException(wait.BecauseOfThisUser
                ? string.Format(Texts.Culture, Texts.Msg_AniList_UserTooFast, seconds)
                : string.Format(Texts.Culture, Texts.Msg_AniList_Busy, seconds));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = JsonContent.Create(new { query = SearchQuery, variables = new { q = query.Trim(), perPage = MaxResults } })
        };
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
                // Weitere Suchen während der Sperre würden sie nur verlängern.
                throttle.BlockFor(TimeSpan.FromSeconds(seconds));
                throw new AniListException(string.Format(Texts.Culture, Texts.Msg_AniList_RateLimited, Math.Ceiling(seconds)));
            }
            if (!response.IsSuccessStatusCode)
                throw new AniListException(string.Format(Texts.Culture, Texts.Msg_AniList_Http, (int)response.StatusCode));

            var body = await response.Content.ReadFromJsonAsync<GraphQlResponse>(ct)
                       ?? throw new AniListException(Texts.Msg_AniList_Empty);
            if (body.Errors is { Count: > 0 })
                throw new AniListException(Texts.Msg_AniList_Failed);

            // GraphQL-Listen dürfen null-Elemente enthalten.
            var results = (body.Data?.Page?.Media ?? [])
                .OfType<Media>()
                .Select(ToAnime)
                .OfType<AniListAnime>()
                .ToList();
            cache.Set(cacheKey, (IReadOnlyList<AniListAnime>)results, CacheDuration);
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new AniListException(Texts.Msg_AniList_Broken, ex);
        }
    }

    /// <summary>Liefert die Bildantwort ungelesen (Aufrufer streamt und entsorgt sie) oder null, wenn es kein Bild gibt.</summary>
    public async Task<HttpResponseMessage?> GetCoverAsync(string coverPath, CancellationToken ct = default)
    {
        if (!AniListCovers.IsValidPath(coverPath))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(CoverBaseUrl, coverPath));
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "image/jpeg" or "image/png" or "image/webp")
                return response;

            response.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static AniListAnime? ToAnime(Media media)
    {
        var romaji = media.Title?.Romaji;
        if (media.Id <= 0 || string.IsNullOrWhiteSpace(romaji))
            return null;

        return new AniListAnime(
            media.Id,
            romaji.Trim(),
            string.IsNullOrWhiteSpace(media.Title?.English) ? null : media.Title.English.Trim(),
            string.IsNullOrWhiteSpace(media.Title?.Native) ? null : media.Title.Native.Trim(),
            media.Format,
            media.SeasonYear ?? media.StartDate?.Year,
            AniListCovers.TryGetPath(media.CoverImage?.Large));
    }

    private sealed record GraphQlResponse(ResponseData? Data, List<JsonElement>? Errors);

    private sealed record ResponseData(PageData? Page);

    private sealed record PageData(List<Media?>? Media);

    private sealed record Media(int Id, string? Format, int? SeasonYear, StartDate? StartDate, Titles? Title, CoverImage? CoverImage);

    private sealed record StartDate(int? Year);

    private sealed record Titles(string? Romaji, string? English, string? Native);

    private sealed record CoverImage(string? Large);
}
