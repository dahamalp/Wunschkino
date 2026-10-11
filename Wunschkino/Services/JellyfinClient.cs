using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wunschkino.Gaps;
using Wunschkino.Library;
using Wunschkino.Resources;

namespace Wunschkino.Services;

public sealed class JellyfinException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class JellyfinClient(HttpClient http)
{
    public const int PageSize = 500;

    // Schutz gegen Endlosschleifen, falls ein Server falsche Gesamtzahlen meldet (entspricht 100.000 Titeln).
    private const int MaxPages = 200;

    public async Task<ConnectionTestResult> TestConnectionAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        if (!ApiKeyFormat.IsValid(apiKey))
            return ConnectionTestResult.Fail(Texts.Msg_Jellyfin_KeyInvalidChars);

        using var request = CreateRequest(baseUrl, "System/Info", apiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ConnectionTestResult.Fail(Texts.Msg_Jellyfin_KeyRejected);
            if (!response.IsSuccessStatusCode)
                return ConnectionTestResult.Fail(string.Format(Texts.Culture, Texts.Msg_Jellyfin_Http, (int)response.StatusCode));

            var info = await response.Content.ReadFromJsonAsync<SystemInfo>(ct);
            return ConnectionTestResult.Ok(string.Format(Texts.Culture, Texts.Msg_Jellyfin_Connected, info?.ServerName, info?.Version));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ConnectionTestResult.Fail(Texts.Msg_Jellyfin_Unreachable);
        }
    }

    /// <exception cref="JellyfinException">Wenn Jellyfin nicht erreichbar ist, den Key ablehnt oder Unlesbares liefert.</exception>
    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        if (!ApiKeyFormat.IsValid(apiKey))
            throw new JellyfinException(Texts.Msg_Jellyfin_StoredKeyInvalid);

        var items = new List<LibraryItem>();
        for (var page = 0; page < MaxPages; page++)
        {
            var path = "Items?IncludeItemTypes=Movie,Series&Recursive=true&Fields=ProviderIds,DateCreated"
                       + $"&EnableImageTypes=Primary&ImageTypeLimit=1&SortBy=SortName&StartIndex={page * PageSize}&Limit={PageSize}";
            var result = await SendForJsonAsync<ItemsResult>(baseUrl, path, apiKey, ct);
            var pageItems = result.Items ?? [];
            items.AddRange(pageItems.Select(ToLibraryItem).OfType<LibraryItem>());

            if (pageItems.Count < PageSize || items.Count >= result.TotalRecordCount)
                return items;
        }
        return items;
    }

    /// <summary>Folgen einer Serie ohne die „fehlenden“ Platzhalter, die Jellyfin selbst anlegen kann.</summary>
    /// <exception cref="JellyfinException">Wenn Jellyfin nicht erreichbar ist, den Key ablehnt oder Unlesbares liefert.</exception>
    public async Task<IReadOnlyList<JellyfinEpisode>> GetEpisodesAsync(string baseUrl, string apiKey, string seriesId, CancellationToken ct = default)
    {
        if (!ApiKeyFormat.IsValid(apiKey))
            throw new JellyfinException(Texts.Msg_Jellyfin_StoredKeyInvalid);

        var path = $"Shows/{Uri.EscapeDataString(seriesId)}/Episodes?IsMissing=false";
        var result = await SendForJsonAsync<EpisodesResult>(baseUrl, path, apiKey, ct);
        return (result.Items ?? [])
            .Where(e => e.LocationType != "Virtual" && e.ParentIndexNumber is not null && e.IndexNumber is not null)
            .Select(e => new JellyfinEpisode(e.ParentIndexNumber!.Value, e.IndexNumber!.Value, e.IndexNumberEnd))
            .ToList();
    }

    /// <summary>Liefert die Bildantwort ungelesen (Aufrufer streamt und entsorgt sie) oder null, wenn es kein Bild gibt.</summary>
    public async Task<HttpResponseMessage?> GetPosterAsync(string baseUrl, string apiKey, string itemId, int maxWidth, CancellationToken ct = default)
    {
        if (!ApiKeyFormat.IsValid(apiKey))
            return null;

        var path = $"Items/{Uri.EscapeDataString(itemId)}/Images/Primary?maxWidth={maxWidth}&quality=90";
        using var request = CreateRequest(baseUrl, path, apiKey);
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                return response;

            response.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<T> SendForJsonAsync<T>(string baseUrl, string relativePath, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(baseUrl, relativePath, apiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new JellyfinException(Texts.Msg_Jellyfin_KeyRejected);
            if (!response.IsSuccessStatusCode)
                throw new JellyfinException(string.Format(Texts.Culture, Texts.Msg_Jellyfin_Http, (int)response.StatusCode));

            return await response.Content.ReadFromJsonAsync<T>(ct)
                   ?? throw new JellyfinException(Texts.Msg_Jellyfin_Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new JellyfinException(Texts.Msg_Jellyfin_Broken, ex);
        }
    }

    private static LibraryItem? ToLibraryItem(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Id) || string.IsNullOrWhiteSpace(item.Name))
            return null;

        MediaKind? kind = item.Type switch
        {
            "Movie" => MediaKind.Movie,
            "Series" => MediaKind.Series,
            _ => null
        };
        if (kind is null)
            return null;

        var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, id) in item.ProviderIds ?? [])
        {
            if (!string.IsNullOrWhiteSpace(id))
                providerIds[provider] = id.Trim();
        }

        return new LibraryItem(
            item.Id,
            item.Name.Trim(),
            kind.Value,
            item.ProductionYear,
            item.DateCreated?.UtcDateTime,
            item.ImageTags?.ContainsKey("Primary") == true,
            providerIds);
    }

    private static HttpRequestMessage CreateRequest(string baseUrl, string relativePath, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), relativePath));
        // Neuere Jellyfin-Versionen akzeptieren standardmäßig nur noch dieses Format (kein X-Emby-Token/api_key mehr).
        request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");
        return request;
    }

    private sealed record SystemInfo(string? ServerName, string? Version);

    private sealed record ItemsResult(List<BaseItem>? Items, int TotalRecordCount);

    private sealed record EpisodesResult(List<EpisodeItem>? Items);

    private sealed record EpisodeItem(int? ParentIndexNumber, int? IndexNumber, int? IndexNumberEnd, string? LocationType);

    private sealed record BaseItem(
        string? Id,
        string? Name,
        string? Type,
        int? ProductionYear,
        DateTimeOffset? DateCreated,
        Dictionary<string, string?>? ProviderIds,
        Dictionary<string, string>? ImageTags);
}
