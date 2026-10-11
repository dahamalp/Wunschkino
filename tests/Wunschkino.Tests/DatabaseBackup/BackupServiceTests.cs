using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Data;
using Wunschkino.DatabaseBackup;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.DatabaseBackup;

public class BackupServiceTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public BackupServiceTests()
    {
        _databasePath = Path.Combine(_dir.Path, "wunschkino.db");
        _backupDirectory = Path.Combine(_dir.Path, "sicherungen");
        using var db = CreateDb();
        db.Database.Migrate();
        db.Users.Add(new User { Username = "anna", PasswordHash = "x", Role = UserRole.Admin });
        db.Settings.Add(new Setting { Key = "Beispiel", Value = "Wert" });
        db.SaveChanges();
    }

    public void Dispose() => _dir.Dispose();

    private AppDbContext CreateDb(string? path = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path ?? _databasePath};Pooling=False").Options);

    private BackupService Create(int keep = 7, int warnAfterDays = 7, string? databasePath = null) =>
        new(new BackupSettings(databasePath ?? _databasePath, _backupDirectory, keep, warnAfterDays), _time, NullLogger<BackupService>.Instance);

    private string[] BackupFiles() =>
        Directory.Exists(_backupDirectory) ? Directory.GetFiles(_backupDirectory).Order().ToArray() : [];

    [Fact]
    public async Task RunNow_CreatesAConsistentCopy_ThatCanBeRestoredAndRead()
    {
        var service = Create();

        Assert.True(await service.RunNowAsync());

        var file = Assert.Single(BackupFiles());
        Assert.Equal("wunschkino-20261004-120000.db", Path.GetFileName(file));
        await using var restored = CreateDb(file);
        Assert.Equal("anna", restored.Users.Single().Username);
        Assert.Equal("Wert", restored.Settings.Single(s => s.Key == "Beispiel").Value);
        Assert.Equal("ok", await ScalarAsync(file, "PRAGMA integrity_check"));
    }

    private static async Task<string?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    [Fact]
    public async Task RunNow_LeavesNoTemporaryFile_AndNeverTouchesTheOriginalDatabase()
    {
        var before = File.ReadAllBytes(_databasePath);

        await Create().RunNowAsync();

        Assert.DoesNotContain(BackupFiles(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(_databasePath));
    }

    [Fact]
    public async Task RunNow_InTheSameSecond_DoesNotCreateASecondFile()
    {
        var service = Create();

        Assert.True(await service.RunNowAsync());
        Assert.True(await service.RunNowAsync());

        Assert.Single(BackupFiles());
    }

    [Fact]
    public async Task OnlyTheNewestBackupsAreKept()
    {
        var service = Create(keep: 3);

        for (var day = 0; day < 6; day++)
        {
            await service.RunNowAsync();
            _time.Advance(TimeSpan.FromDays(1));
        }

        Assert.Equal(
            ["wunschkino-20261007-120000.db", "wunschkino-20261008-120000.db", "wunschkino-20261009-120000.db"],
            BackupFiles().Select(Path.GetFileName));
    }

    [Fact]
    public async Task FilesThatAreNotBackups_AreNeitherCountedNorDeleted()
    {
        Directory.CreateDirectory(_backupDirectory);
        File.WriteAllText(Path.Combine(_backupDirectory, "notizen.txt"), "wichtig");
        File.WriteAllText(Path.Combine(_backupDirectory, "wunschkino-kaputt.db"), "kein Datum");
        var service = Create(keep: 1);

        await service.RunNowAsync();
        _time.Advance(TimeSpan.FromDays(1));
        await service.RunNowAsync();

        Assert.Contains(BackupFiles(), f => Path.GetFileName(f) == "notizen.txt");
        Assert.Contains(BackupFiles(), f => Path.GetFileName(f) == "wunschkino-kaputt.db");
        Assert.Equal(1, service.GetStatus().Count);
    }

    [Fact]
    public async Task RunIfDue_CreatesTheFirstBackup_ButNotAgainWithin24Hours()
    {
        var service = Create();

        Assert.True(await service.RunIfDueAsync());
        _time.Advance(TimeSpan.FromHours(23));
        Assert.False(await service.RunIfDueAsync());
        _time.Advance(TimeSpan.FromHours(2));
        Assert.True(await service.RunIfDueAsync());

        Assert.Equal(2, BackupFiles().Length);
    }

    [Fact]
    public async Task AFailedBackup_ReturnsFalse_ReportsTheError_AndLeavesNothingBehind()
    {
        var service = Create(databasePath: Path.Combine(_dir.Path, "gibtsnicht.db"));

        Assert.False(await service.RunNowAsync());

        var status = service.GetStatus();
        Assert.NotNull(status.LastError);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, status.LastErrorUtc);
        Assert.Empty(BackupFiles());
        Assert.False(File.Exists(Path.Combine(_dir.Path, "gibtsnicht.db")), "Die Sicherung darf keine leere Datenbank anlegen.");
    }

    [Fact]
    public async Task AFailedBackup_IsForgotten_OnceTheNextOneSucceeds()
    {
        var failing = Create(databasePath: Path.Combine(_dir.Path, "gibtsnicht.db"));
        await failing.RunNowAsync();
        Assert.NotNull(failing.GetStatus().LastError);

        var working = Create();
        await working.RunNowAsync();

        Assert.Null(working.GetStatus().LastError);
    }

    [Fact]
    public void Status_WithoutAnyBackup_IsOverdue_AndEmpty()
    {
        var status = Create().GetStatus();

        Assert.True(status.IsOverdue);
        Assert.Null(status.LastBackupUtc);
        Assert.Equal(0, status.Count);
        Assert.Equal(0, status.TotalBytes);
        Assert.Equal(_backupDirectory, status.Directory);
    }

    [Fact]
    public async Task Status_ReportsTimeCountAndSize_AndBecomesOverdueAfterTheWarnPeriod()
    {
        var service = Create(warnAfterDays: 3);
        await service.RunNowAsync();
        var backupTime = _time.GetUtcNow().UtcDateTime;

        var fresh = service.GetStatus();
        Assert.False(fresh.IsOverdue);
        Assert.Equal(backupTime, fresh.LastBackupUtc);
        Assert.Equal(1, fresh.Count);
        Assert.True(fresh.TotalBytes > 0);

        _time.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        Assert.True(service.GetStatus().IsOverdue);
    }

    [Fact]
    public async Task ParallelRuns_ProduceOneValidBackup()
    {
        var service = Create();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.RunNowAsync()));

        Assert.All(results, Assert.True);
        var file = Assert.Single(BackupFiles());
        Assert.Equal("ok", await ScalarAsync(file, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task TheBackupFolder_IsCreatedOnDemand_AndHoldsOnlyDatabaseCopies()
    {
        Assert.False(Directory.Exists(_backupDirectory));

        await Create().RunNowAsync();

        Assert.All(BackupFiles(), f => Assert.EndsWith(".db", f));
        Assert.DoesNotContain(BackupFiles(), f => f.Contains("keys", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OnUnix_TheBackupIsReadableByTheOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        await Create().RunNowAsync();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Assert.Single(BackupFiles())));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_backupDirectory));
    }
}
