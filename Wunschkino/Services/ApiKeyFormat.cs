namespace Wunschkino.Services;

public static class ApiKeyFormat
{
    /// <summary>Nur sichtbare ASCII-Zeichen – verhindert u. a. Header-Injection per CR/LF, wenn der Key in einen HTTP-Header wandert.</summary>
    public static bool IsValid(string apiKey) =>
        apiKey.Length is > 0 and <= 1024 && apiKey.All(c => c is > ' ' and <= '~');
}
