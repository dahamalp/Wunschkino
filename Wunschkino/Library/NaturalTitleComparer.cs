using System.Globalization;

namespace Wunschkino.Library;

/// <summary>
/// Vergleicht Titel so, wie Menschen sie ordnen: Zahlenfolgen nach ihrem Wert („Staffel 2“ vor „Staffel 10“), der Rest
/// kulturabhängig ohne Groß-/Kleinschreibung. Bewusst selbst geschrieben statt <c>CompareOptions.NumericOrdering</c>,
/// damit die Reihenfolge nicht davon abhängt, ob das Zielsystem ICU mitbringt.
/// </summary>
public sealed class NaturalTitleComparer : IComparer<string>
{
    public static readonly NaturalTitleComparer Instance = new();

    private static readonly CompareInfo Culture = CultureInfo.CurrentCulture.CompareInfo;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var i = 0;
        var j = 0;
        var leadingZeroTieBreak = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var digitsX = TakeWhile(x, ref i, digits: true);
                var digitsY = TakeWhile(y, ref j, digits: true);
                var valueX = digitsX.TrimStart('0');
                var valueY = digitsY.TrimStart('0');

                // Ziffernfolgen werden als Text verglichen, damit auch sehr lange Zahlen nicht überlaufen.
                if (valueX.Length != valueY.Length)
                    return valueX.Length.CompareTo(valueY.Length);
                var byValue = valueX.CompareTo(valueY, StringComparison.Ordinal);
                if (byValue != 0)
                    return byValue;
                if (leadingZeroTieBreak == 0)
                    leadingZeroTieBreak = digitsX.Length.CompareTo(digitsY.Length);
            }
            else
            {
                var textX = TakeWhile(x, ref i, digits: false);
                var textY = TakeWhile(y, ref j, digits: false);
                var byText = Culture.Compare(textX, textY, CompareOptions.IgnoreCase);
                if (byText != 0)
                    return byText;
            }
        }

        var remaining = (x.Length - i).CompareTo(y.Length - j);
        return remaining != 0 ? remaining : leadingZeroTieBreak;
    }

    private static ReadOnlySpan<char> TakeWhile(string text, ref int index, bool digits)
    {
        var start = index;
        while (index < text.Length && char.IsAsciiDigit(text[index]) == digits)
            index++;
        return text.AsSpan(start, index - start);
    }
}
