using Wunschkino.Resources;
namespace Wunschkino.Services;

/// <summary>Gemeinsame Prüfregeln für Verbindungsdaten im Einrichtungsassistenten und auf der Einstellungsseite.</summary>
public static class ConnectionValidation
{
    public static string? ValidateJellyfinUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Texts.Msg_Connection_UrlFormat;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return Texts.Msg_Connection_UrlParts;
        return null;
    }

    public static string? ValidateApiKey(string? apiKey, string name) =>
        string.IsNullOrWhiteSpace(apiKey) ? string.Format(Texts.Culture, Texts.Msg_Connection_KeyMissing, name)
        : !ApiKeyFormat.IsValid(apiKey.Trim()) ? string.Format(Texts.Culture, Texts.Msg_Connection_KeyInvalid, name)
        : null;

    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');
}
