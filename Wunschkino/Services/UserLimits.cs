using System.Globalization;

namespace Wunschkino.Services;

/// <summary>
/// Mengenlimits je Nutzer, vom Admin unter „Einstellungen“ einstellbar. Der Wert 0 heißt „kein Limit“. Ein fehlender oder
/// unlesbarer Wert fällt auf den Standard zurück, damit eine kaputte Einstellung nie alle Nutzer aussperrt oder alles freigibt.
/// </summary>
public static class UserLimits
{
    public const int DefaultWishesPerDay = 20;
    public const int MaxWishesPerDay = 1000;
    public const int DefaultSearchesPerMinute = 10;
    public const int MaxSearchesPerMinute = 120;

    public static int ParseWishesPerDay(string? stored) => Parse(stored, DefaultWishesPerDay, MaxWishesPerDay);

    public static int ParseSearchesPerMinute(string? stored) => Parse(stored, DefaultSearchesPerMinute, MaxSearchesPerMinute);

    public static int ClampWishesPerDay(int value) => Math.Clamp(value, 0, MaxWishesPerDay);

    public static int ClampSearchesPerMinute(int value) => Math.Clamp(value, 0, MaxSearchesPerMinute);

    private static int Parse(string? stored, int fallback, int max) =>
        int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Min(value, max) : fallback;
}
