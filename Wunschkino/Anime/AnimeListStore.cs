namespace Wunschkino.Anime;

/// <summary>Legt die heruntergeladene Zuordnungsliste samt ETag im Datenordner ab, damit sie einen Neustart überdauert.</summary>
public sealed class AnimeListStore(string dataDirectory)
{
    private string ListPath => Path.Combine(dataDirectory, "anime-list.json");
    private string ETagPath => Path.Combine(dataDirectory, "anime-list.etag");

    public byte[]? ReadList() => File.Exists(ListPath) ? File.ReadAllBytes(ListPath) : null;

    public DateTime? LastWriteUtc => File.Exists(ListPath) ? File.GetLastWriteTimeUtc(ListPath) : null;

    public string? ReadETag() => File.Exists(ETagPath) ? File.ReadAllText(ETagPath).Trim() : null;

    /// <summary>Schreibt erst in Hilfsdateien und tauscht dann aus, damit ein Abbruch nie eine halbe Liste hinterlässt.</summary>
    public void Write(byte[] content, string? etag)
    {
        Replace(ListPath, content);
        if (etag is null)
            File.Delete(ETagPath);
        else
            Replace(ETagPath, System.Text.Encoding.UTF8.GetBytes(etag));
    }

    private static void Replace(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
