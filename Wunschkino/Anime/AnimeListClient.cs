using System.Net;
using System.Text.RegularExpressions;
using Wunschkino.Resources;

namespace Wunschkino.Anime;

public sealed class AnimeListException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record AnimeListDownload(bool NotModified, byte[]? Content, string? ETag);

public sealed partial class AnimeListClient(HttpClient http)
{
    public static readonly Uri ListUrl = new("https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json");

    // Die Liste ist rund 6 MB groß. Die Obergrenze schützt vor einer unerwartet riesigen Antwort (das Parsen
    // braucht kurzzeitig ein Vielfaches an Speicher).
    public const long MaxBytes = 20_000_000;

    /// <summary>
    /// Gilt für den ganzen Download. Der Timeout des HttpClient endet mit den Headern; hängt der Server danach
    /// mitten im Body, würde der Updater sonst nie mehr zum nächsten Lauf kommen.
    /// </summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <exception cref="AnimeListException">Wenn der Download scheitert, zu lange dauert oder zu groß ist.</exception>
    public async Task<AnimeListDownload> DownloadAsync(string? etag, CancellationToken callerToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ListUrl);
        // Der ETag stammt aus einer Datei; nur die erwartete Form kommt in den Header.
        if (etag is not null && ETagPattern().IsMatch(etag))
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(DownloadTimeout);
        var ct = timeout.Token;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new AnimeListDownload(true, null, etag);
            if (!response.IsSuccessStatusCode)
                throw new AnimeListException(string.Format(Texts.Culture, Texts.Msg_AnimeList_Http, (int)response.StatusCode));
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new AnimeListException(Texts.Msg_AnimeList_TooLarge);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    throw new AnimeListException(Texts.Msg_AnimeList_TooLarge);
                buffer.Write(chunk, 0, read);
            }
            return new AnimeListDownload(false, buffer.ToArray(), response.Headers.ETag?.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !callerToken.IsCancellationRequested))
        {
            throw new AnimeListException(Texts.Msg_AnimeList_Unreachable, ex);
        }
    }

    [GeneratedRegex("^(W/)?\"[\\x21\\x23-\\x7E]*\"\\z")]
    private static partial Regex ETagPattern();
}
