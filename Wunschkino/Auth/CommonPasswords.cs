using System.IO.Compression;

namespace Wunschkino.Auth;

/// <summary>
/// Häufige, aus Datenlecks bekannte Passwörter ab der Mindestlänge (aus SecLists, MIT-Lizenz). Die Prüfung läuft rein lokal:
/// Ein Abgleich über einen Online-Dienst würde Teile des Passworts an einen fremden Server schicken.
/// Groß-/Kleinschreibung zählt nicht, „Password1234!“ ist so schwach wie „password1234!“.
/// </summary>
public static class CommonPasswords
{
    private const string ResourceName = "Wunschkino.Auth.common-passwords.txt.gz";

    // PublicationOnly: Scheitert das Laden einmal, merkt sich Lazy die Ausnahme nicht für immer.
    private static readonly Lazy<HashSet<string>> List = new(Load, LazyThreadSafetyMode.PublicationOnly);
    private static volatile bool _enabled = true;

    /// <summary>Wird beim Start aus <c>Wunschkino:CheckCommonPasswords</c> gesetzt.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static int Count => List.Value.Count;

    public static bool Contains(string password) => List.Value.Contains(password.ToLowerInvariant());

    private static HashSet<string> Load()
    {
        using var resource = typeof(CommonPasswords).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Ressource {ResourceName} fehlt.");
        using var reader = new StreamReader(new GZipStream(resource, CompressionMode.Decompress));
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                set.Add(line);
        }
        return set;
    }
}
