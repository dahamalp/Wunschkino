using Microsoft.AspNetCore.Components;

namespace Wunschkino.Services;

/// <summary>Filterwerte in der Adresszeile: damit überleben sie Neuladen und lassen sich als Link teilen.</summary>
public static class UrlFilter
{
    public const int MaxTextLength = 100;

    /// <summary>Liest einen Enum-Wert (ohne Beachtung der Groß-/Kleinschreibung); Zahlen, unbekannte und leere Werte ergeben null.</summary>
    public static T? ParseEnum<T>(string? value) where T : struct, Enum
    {
        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<T>(name);
        }

        return null;
    }

    public static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length <= MaxTextLength)
            return value;

        var length = char.IsHighSurrogate(value[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return value[..length];
    }

    /// <summary>Wert für die URL; der Standardwert wird weggelassen, damit saubere Adressen entstehen.</summary>
    public static string? Write<T>(T? value, T? defaultValue = null) where T : struct, Enum =>
        value is { } v && !EqualityComparer<T?>.Default.Equals(v, defaultValue) ? v.ToString().ToLowerInvariant() : null;

    public static void Replace(NavigationManager navigation, IReadOnlyDictionary<string, object?> parameters) =>
        navigation.NavigateTo(navigation.GetUriWithQueryParameters(parameters), replace: true);
}
