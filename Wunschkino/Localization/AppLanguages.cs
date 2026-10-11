using System.Globalization;

namespace Wunschkino.Localization;

public sealed record AppLanguage(string Code, string NativeName, string TmdbCode);

/// <summary>
/// Die Sprachen der Oberfläche. Deutsch ist die Grundsprache (<c>Texts.resx</c>), jede weitere hat eine eigene
/// <c>Texts.&lt;Code&gt;.resx</c>. Wer eine Sprache ergänzt, trägt sie hier ein und legt die Datei an.
/// </summary>
public static class AppLanguages
{
    public const string Default = "de";

    public static readonly IReadOnlyList<AppLanguage> All =
    [
        new("de", "Deutsch", "de-DE"),
        new("en", "English", "en-US"),
        new("fr", "Français", "fr-FR"),
        new("es", "Español", "es-ES"),
        new("nl", "Nederlands", "nl-NL")
    ];

    public static IReadOnlyList<CultureInfo> Cultures { get; } = All.Select(l => CultureInfo.GetCultureInfo(l.Code)).ToList();

    /// <summary>Normalisiert eine Angabe wie „en-GB“ oder „FR“ auf einen unterstützten Code, sonst null.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var language = code.Trim().Split('-', '_')[0].ToLowerInvariant();
        return All.Any(l => l.Code == language) ? language : null;
    }

    /// <summary>Sprache für TMDB-Anfragen passend zur aktuellen Oberfläche (Titel und Beschreibungen kommen dann in dieser Sprache).</summary>
    public static string TmdbCode(CultureInfo culture) =>
        All.FirstOrDefault(l => l.Code == culture.TwoLetterISOLanguageName)?.TmdbCode ?? All[0].TmdbCode;
}
