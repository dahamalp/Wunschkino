using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wunschkino.Gaps;
using Wunschkino.Library;
using Wunschkino.Localization;
using Wunschkino.Resources;

namespace Wunschkino.Services;

public class TmdbException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record TmdbTitle(int TmdbId, MediaKind Kind, string Title, int? Year, string? PosterPath, string? ImdbId);

public sealed class TmdbClient(HttpClient http)
{
    public static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");
    public static readonly Uri ImageBaseAddress = new("https://image.tmdb.org/t/p/");

    private static string Language => AppLanguages.TmdbCode(CultureInfo.CurrentUICulture);

    // Schutz gegen absurde Antworten: Keine reale Serie hat annähernd so viele Staffeln.
    private const int MaxSeasons = 100;

    public async Task<ConnectionTestResult> TestConnectionAsync(string apiKey, CancellationToken ct = default)
    {
        if (!ApiKeyFormat.IsValid(apiKey.Trim()))
            return ConnectionTestResult.Fail(Texts.Msg_Tmdb_KeyInvalidChars);

        using var request = CreateRequest("authentication", apiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ConnectionTestResult.Fail(Texts.Msg_Tmdb_KeyRejected);
            return response.IsSuccessStatusCode
                ? ConnectionTestResult.Ok(Texts.Msg_Tmdb_KeyValid)
                : ConnectionTestResult.Fail(string.Format(Texts.Culture, Texts.Msg_Tmdb_Http, (int)response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ConnectionTestResult.Fail(Texts.Msg_Tmdb_Unreachable);
        }
    }

    /// <summary>Sucht Filme und Serien (Personen und anderes werden verworfen).</summary>
    /// <exception cref="TmdbException">Wenn TMDB nicht erreichbar ist, den Key ablehnt oder Unlesbares liefert.</exception>
    public async Task<IReadOnlyList<TmdbTitle>> SearchAsync(string apiKey, string query, CancellationToken ct = default)
    {
        var result = await GetJsonAsync<SearchResponse>(
            apiKey, $"search/multi?query={Uri.EscapeDataString(query)}&language={Language}&include_adult=false", ct);
        return (result.Results ?? [])
            .Select(r => ToTitle(r, r.MediaType switch { "movie" => MediaKind.Movie, "tv" => MediaKind.Series, _ => null }))
            .OfType<TmdbTitle>()
            .ToList();
    }

    /// <exception cref="TmdbException">Wenn TMDB nicht erreichbar ist, den Key ablehnt oder Unlesbares liefert.</exception>
    public async Task<IReadOnlyList<TmdbTitle>> FindByImdbAsync(string apiKey, string imdbId, CancellationToken ct = default)
    {
        var result = await GetJsonAsync<FindResponse>(
            apiKey, $"find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id&language={Language}", ct);
        return (result.MovieResults ?? []).Select(r => ToTitle(r, MediaKind.Movie, imdbId))
            .Concat((result.TvResults ?? []).Select(r => ToTitle(r, MediaKind.Series, imdbId)))
            .OfType<TmdbTitle>()
            .ToList();
    }

    /// <summary>Liefert null, wenn es den Titel bei TMDB nicht gibt.</summary>
    /// <exception cref="TmdbException">Wenn TMDB nicht erreichbar ist, den Key ablehnt oder Unlesbares liefert.</exception>
    /// <param name="language">TMDB-Sprachcode; ohne Angabe die Sprache der aktuellen Oberfläche.</param>
    public async Task<TmdbTitle?> GetDetailsAsync(string apiKey, int tmdbId, MediaKind kind, CancellationToken ct = default, string? language = null)
    {
        var segment = kind == MediaKind.Movie ? "movie" : "tv";
        try
        {
            var item = await GetJsonAsync<SearchItem>(apiKey, $"{segment}/{tmdbId}?language={language ?? Language}&append_to_response=external_ids", ct);
            return ToTitle(item with { Id = item.Id == 0 ? tmdbId : item.Id }, kind, item.ImdbId ?? item.ExternalIds?.ImdbId);
        }
        catch (TmdbNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Alle Folgen der Staffeln ab 1 mit Sendedatum (Specials sind nicht gefragt); null, wenn es die Serie bei TMDB nicht gibt.</summary>
    /// <exception cref="TmdbException">Wenn TMDB nicht erreichbar ist, den Key ablehnt oder Unlesbares liefert.</exception>
    public async Task<IReadOnlyList<TmdbEpisode>?> GetEpisodesAsync(string apiKey, int tmdbId, CancellationToken ct = default)
    {
        TvSeasons details;
        try
        {
            details = await GetJsonAsync<TvSeasons>(apiKey, $"tv/{tmdbId}", ct);
        }
        catch (TmdbNotFoundException)
        {
            return null;
        }

        var episodes = new List<TmdbEpisode>();
        var seasonNumbers = (details.Seasons ?? []).Select(s => s.SeasonNumber).Where(n => n > 0).Distinct().Order().Take(MaxSeasons);
        foreach (var seasonNumber in seasonNumbers)
        {
            try
            {
                var season = await GetJsonAsync<SeasonEpisodes>(apiKey, $"tv/{tmdbId}/season/{seasonNumber}", ct);
                foreach (var episode in (season.Episodes ?? []).Where(e => e.EpisodeNumber > 0))
                    episodes.Add(new TmdbEpisode(seasonNumber, episode.EpisodeNumber, ParseAirDate(episode.AirDate)));
            }
            catch (TmdbNotFoundException)
            {
                // Eine Staffel ohne Daten bei TMDB liefert keine Soll-Folgen; sie darf nicht als „fehlend“ gelten.
            }
        }
        return episodes;
    }

    private static DateOnly? ParseAirDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date) ? date : null;

    /// <summary>Liefert die Bildantwort ungelesen (Aufrufer streamt und entsorgt sie) oder null, wenn es kein Bild gibt.</summary>
    public async Task<HttpResponseMessage?> GetPosterAsync(string posterPath, TmdbPosterSize size, CancellationToken ct = default)
    {
        if (!TmdbPosters.IsValidPath(posterPath))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ImageBaseAddress, $"{TmdbPosters.SizeSegment(size)}{posterPath}"));
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "image/jpeg" or "image/png")
                return response;

            response.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<T> GetJsonAsync<T>(string apiKey, string relativePath, CancellationToken ct)
    {
        if (!ApiKeyFormat.IsValid(apiKey.Trim()))
            throw new TmdbException(Texts.Msg_Tmdb_StoredKeyInvalid);

        using var request = CreateRequest(relativePath, apiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new TmdbException(Texts.Msg_Tmdb_KeyRejected);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new TmdbNotFoundException();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new TmdbException(Texts.Msg_Tmdb_RateLimited);
            if (!response.IsSuccessStatusCode)
                throw new TmdbException(string.Format(Texts.Culture, Texts.Msg_Tmdb_Http, (int)response.StatusCode));

            return await response.Content.ReadFromJsonAsync<T>(ct)
                   ?? throw new TmdbException(Texts.Msg_Tmdb_Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new TmdbException(Texts.Msg_Tmdb_Broken, ex);
        }
    }

    private static TmdbTitle? ToTitle(SearchItem item, MediaKind? kind, string? imdbId = null)
    {
        var title = kind == MediaKind.Series ? item.Name : item.Title;
        if (kind is null || item.Id <= 0 || string.IsNullOrWhiteSpace(title))
            return null;

        var date = kind == MediaKind.Series ? item.FirstAirDate : item.ReleaseDate;
        int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var parsed) ? parsed : null;
        // Der Pfad landet später in einer Proxy-URL und in der DB – nur das erwartete Format wird übernommen.
        var poster = item.PosterPath is { } path && TmdbPosters.IsValidPath(path) ? path : null;
        return new TmdbTitle(item.Id, kind.Value, title.Trim(), year, poster, string.IsNullOrWhiteSpace(imdbId) ? null : imdbId.Trim());
    }

    /// <summary>
    /// TMDB vergibt zwei Arten von Zugangsdaten: den v4-Lesezugriffs-Token (JWT, als Bearer) und den
    /// v3-API-Key (als Query-Parameter). Beide werden akzeptiert, damit der Admin einfach einfügen kann, was er hat.
    /// </summary>
    private static HttpRequestMessage CreateRequest(string relativePath, string apiKey)
    {
        var key = apiKey.Trim();
        if (IsReadAccessToken(key))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }
        var separator = relativePath.Contains('?') ? '&' : '?';
        return new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress, $"{relativePath}{separator}api_key={Uri.EscapeDataString(key)}"));
    }

    private static bool IsReadAccessToken(string key) => key.StartsWith("eyJ", StringComparison.Ordinal) && key.Count(c => c == '.') == 2;

    private sealed class TmdbNotFoundException() : TmdbException(Texts.Msg_Tmdb_NotFound);

    private sealed record SearchResponse(List<SearchItem>? Results);

    private sealed record FindResponse(
        [property: JsonPropertyName("movie_results")] List<SearchItem>? MovieResults,
        [property: JsonPropertyName("tv_results")] List<SearchItem>? TvResults);

    private sealed record ExternalIds([property: JsonPropertyName("imdb_id")] string? ImdbId);

    private sealed record TvSeasons(List<SeasonEntry>? Seasons);

    private sealed record SeasonEntry([property: JsonPropertyName("season_number")] int SeasonNumber);

    private sealed record SeasonEpisodes(List<EpisodeEntry>? Episodes);

    private sealed record EpisodeEntry(
        [property: JsonPropertyName("episode_number")] int EpisodeNumber,
        [property: JsonPropertyName("air_date")] string? AirDate);

    private sealed record SearchItem(
        int Id,
        [property: JsonPropertyName("media_type")] string? MediaType,
        string? Title,
        string? Name,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("imdb_id")] string? ImdbId,
        [property: JsonPropertyName("external_ids")] ExternalIds? ExternalIds);
}
