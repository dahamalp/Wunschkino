using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Wunschkino.DatabaseBackup;

/// <param name="DatabasePath">Die Datenbankdatei der App.</param>
/// <param name="Directory">Ordner der Sicherungen.</param>
/// <param name="Keep">So viele Sicherungen bleiben liegen, ältere werden gelöscht.</param>
/// <param name="WarnAfterDays">Ab so vielen Tagen ohne neue Sicherung warnt die App.</param>
public sealed record BackupSettings(string DatabasePath, string Directory, int Keep, int WarnAfterDays);

public sealed record BackupStatus(
    string Directory,
    DateTime? LastBackupUtc,
    int Count,
    long TotalBytes,
    bool SameMountAsDatabase,
    bool IsOverdue,
    string? LastError,
    DateTime? LastErrorUtc,
    bool DirectoryReadableByOthers = false);

/// <summary>
/// Sichert die Datenbank täglich mit <c>VACUUM INTO</c> (eine in sich stimmige Kopie, auch während die App läuft) in einen eigenen
/// Ordner und behält die letzten Sicherungen. Gesichert wird bewusst nur die Datenbank, nie der Schlüsselordner <c>keys/</c>: Die
/// API-Schlüssel in der Datenbank sind mit diesen Schlüsseln verschlüsselt, und beides ungeschützt zusammen zu sichern höbe die
/// Verschlüsselung auf. Den Schlüsselordner sichert der Betreiber getrennt (siehe README).
/// </summary>
public sealed class BackupService(
    BackupSettings settings,
    TimeProvider time,
    ILogger<BackupService> logger,
    IReadOnlyList<string>? mountPoints = null) : BackgroundService
{
    public const string FilePrefix = "wunschkino-";
    public const string FileExtension = ".db";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastError;
    private DateTime? _lastErrorUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunIfDueAsync(stoppingToken);
                await Task.Delay(CheckInterval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Die App wird beendet.
        }
    }

    /// <returns>Wahr, wenn jetzt eine neue Sicherung entstand.</returns>
    public async Task<bool> RunIfDueAsync(CancellationToken ct = default)
    {
        var last = FindBackups().Select(b => b.TimeUtc).DefaultIfEmpty().Max();
        var now = time.GetUtcNow().UtcDateTime;
        return (last == default || now - last >= Interval) && await RunNowAsync(ct);
    }

    /// <returns>Wahr, wenn die Sicherung gelungen ist.</returns>
    public async Task<bool> RunNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var temporary = "";
        try
        {
            EnsureDirectory();
            var target = Path.Combine(settings.Directory, FilePrefix + now.ToString(StampFormat, CultureInfo.InvariantCulture) + FileExtension);
            temporary = target + ".tmp";
            if (File.Exists(target))
                return true;
            File.Delete(temporary);
            CreateEmptyPrivateFile(temporary);

            await using (var connection = new SqliteConnection($"Data Source={settings.DatabasePath};Mode=ReadOnly;Pooling=False"))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $target";
                command.Parameters.AddWithValue("$target", temporary);
                await command.ExecuteNonQueryAsync(ct);
            }

            File.Move(temporary, target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Prune();
            _lastError = null;
            _lastErrorUtc = null;
            logger.LogInformation("Datenbank gesichert: {File}", target);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            _lastErrorUtc = now;
            logger.LogError(ex, "Die Datensicherung ist fehlgeschlagen.");
            TryDelete(temporary);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public BackupStatus GetStatus()
    {
        var backups = FindBackups();
        var now = time.GetUtcNow().UtcDateTime;
        DateTime? last = backups.Count == 0 ? null : backups.Max(b => b.TimeUtc);
        return new BackupStatus(
            settings.Directory,
            last,
            backups.Count,
            backups.Sum(b => b.Bytes),
            // mountPoints nur in Tests; sonst liest MountPoints die Einbindungen des Systems.
            MountPoints.OnSameMount(settings.Directory, settings.DatabasePath, mountPoints),
            last is null || now - last.Value > TimeSpan.FromDays(settings.WarnAfterDays),
            _lastError,
            _lastErrorUtc,
            IsReadableByOthers(settings.Directory));
    }

    /// <summary>
    /// Legt die Zieldatei vorab mit 0600 an: <c>VACUUM INTO</c> akzeptiert eine leere Datei, und so ist die Kopie der Datenbank
    /// von Anfang an nur für den Dienstbenutzer lesbar, nicht erst nach dem Umbenennen.
    /// </summary>
    private static void CreateEmptyPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private static bool IsReadableByOthers(string directory)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(directory))
            return false;

        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(directory) & others) != 0;
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(settings.Directory);
        else
            Directory.CreateDirectory(settings.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private List<(string Path, DateTime TimeUtc, long Bytes)> FindBackups()
    {
        if (!Directory.Exists(settings.Directory))
            return [];

        var result = new List<(string, DateTime, long)>();
        foreach (var path in Directory.EnumerateFiles(settings.Directory, FilePrefix + "*" + FileExtension))
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
                continue;

            var name = Path.GetFileNameWithoutExtension(path)[FilePrefix.Length..];
            if (DateTime.TryParseExact(name, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp))
                result.Add((path, stamp, info.Length));
        }
        return result.OrderByDescending(b => b.Item2).ToList();
    }

    private void Prune()
    {
        foreach (var old in FindBackups().Skip(Math.Max(1, settings.Keep)))
            TryDelete(old.Path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (path.Length > 0)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Eine Leiche mehr im Ordner ist harmlos; der nächste Lauf räumt wieder auf.
        }
    }
}
