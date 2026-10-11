using System.Text.RegularExpressions;

namespace Wunschkino.Wishes;

public static partial class WishValidation
{
    public const int MaxQueryLength = 100;
    public const int MaxNoteLength = 300;
    public const int MaxCommentLength = 500;

    public static bool IsImdbId(string query) => ImdbPattern().IsMatch(query);

    /// <summary>Macht aus Steuerzeichen und Leerraumfolgen ein Leerzeichen; liefert null für leere und für zu lange Texte (<paramref name="tooLong"/>).</summary>
    public static string? NormalizeText(string? text, int maxLength, out bool tooLong)
    {
        var cleaned = text is null ? "" : WhitespacePattern().Replace(text, " ").Trim();
        tooLong = cleaned.Length > maxLength;
        return cleaned.Length == 0 || tooLong ? null : cleaned;
    }

    [GeneratedRegex(@"[\s\p{C}]+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^tt\d{7,10}$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbPattern();
}
