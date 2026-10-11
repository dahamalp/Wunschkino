namespace Wunschkino.DatabaseBackup;

/// <summary>
/// Erkennt, ob zwei Ordner auf demselben Laufwerk (derselben Einbindung) liegen. Eine Sicherung auf demselben Laufwerk wie die
/// Datenbank schützt nicht vor einem Plattenausfall, deshalb weist die App darauf hin.
/// </summary>
public static class MountPoints
{
    public static bool OnSameMount(string pathA, string pathB, IReadOnlyList<string>? mountPoints = null)
    {
        var mounts = mountPoints ?? ReadMountPoints();
        return string.Equals(OwnerOf(pathA, mounts), OwnerOf(pathB, mounts), StringComparison.Ordinal);
    }

    /// <summary>Die Einbindung, zu der ein Pfad gehört: der längste passende Einbindungspunkt.</summary>
    public static string OwnerOf(string path, IReadOnlyList<string> mountPoints)
    {
        var normalized = Normalize(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var best = "";
        foreach (var mount in mountPoints.Select(Normalize))
        {
            if (mount.Length > best.Length && IsInside(normalized, mount, comparison))
                best = mount;
        }
        return best;
    }

    /// <summary>Liest die Einbindungspunkte: unter Linux aus <c>/proc/mounts</c>, sonst die Laufwerke des Systems.</summary>
    public static IReadOnlyList<string> ReadMountPoints()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/mounts"))
                return ParseProcMounts(File.ReadAllLines("/proc/mounts"));

            return DriveInfo.GetDrives().Select(drive => drive.Name).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ParseProcMounts(IEnumerable<string> lines) =>
        lines.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 2)
            // Leerzeichen in Pfaden stehen in /proc/mounts als \040.
            .Select(fields => fields[1].Replace("\\040", " ", StringComparison.Ordinal))
            .ToList();

    private static bool IsInside(string path, string mount, StringComparison comparison) =>
        mount.Length == 0 || path.Equals(mount, comparison) || path.StartsWith(mount + "/", comparison) || mount == "/";

    private static string Normalize(string path)
    {
        var full = path.Replace('\\', '/');
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }
}
