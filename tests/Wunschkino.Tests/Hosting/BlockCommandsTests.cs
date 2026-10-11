using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Hosting;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Hosting;

public class BlockCommandsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly string _databasePath;

    public BlockCommandsTests()
    {
        _databasePath = Path.Combine(_directory.Path, "wunschkino.db");
        using var db = CreateDb();
        db.Database.Migrate();
    }

    public void Dispose() => _directory.Dispose();

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=False").Options);

    private void Add(string address, int failures, DateTime? until = null, bool permanent = false)
    {
        using var db = CreateDb();
        db.IpBlocks.Add(new IpBlock
        {
            Address = address, Failures = failures, BlockedUntilUtc = until, IsPermanent = permanent, LastFailureUtc = Now.AddMinutes(-5)
        });
        db.SaveChanges();
    }

    private int Count()
    {
        using var db = CreateDb();
        return db.IpBlocks.Count();
    }

    private Task<int> RunAsync(params string[] args) => BlockCommands.RunAsync(args, _databasePath, _output, _error, _time);

    [Theory]
    [InlineData("--list-blocks", true)]
    [InlineData("--unblock", true)]
    [InlineData("--unblock-all", true)]
    [InlineData("--validate-config", false)]
    [InlineData("--port", false)]
    public void OnlyTheBlockSwitches_AreRecognizedAsCommands(string arg, bool expected)
    {
        Assert.Equal(expected, BlockCommands.IsBlockCommand([arg]));
    }

    [Fact]
    public async Task List_ShowsEveryStateReadably()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(1));
        Add("203.0.113.2", 18, permanent: true);
        Add("203.0.113.3", 2);

        var exitCode = await RunAsync("--list-blocks");

        Assert.Equal(0, exitCode);
        var text = _output.ToString();
        Assert.Contains("203.0.113.1", text);
        Assert.Contains("gesperrt bis 2026-10-03 12:01 UTC", text);
        Assert.Contains("dauerhaft gesperrt", text);
        Assert.Contains("nicht gesperrt", text);
    }

    [Fact]
    public async Task List_OnAnEmptyTable_SaysSo()
    {
        var exitCode = await RunAsync("--list-blocks");

        Assert.Equal(0, exitCode);
        Assert.Contains("Keine Adresse", _output.ToString());
    }

    [Fact]
    public async Task Unblock_RemovesTheAddress_AndKeepsTheOthers()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(1));
        Add("203.0.113.2", 18, permanent: true);

        var exitCode = await RunAsync("--unblock", "203.0.113.2");

        Assert.Equal(0, exitCode);
        Assert.Contains("203.0.113.2 entsperrt", _output.ToString());
        Assert.Equal(1, Count());
    }

    [Fact]
    public async Task Unblock_OfAnIpv6Address_RemovesItsWholePrefix()
    {
        Add("2001:db8:1:2::/64", 18, permanent: true);

        var exitCode = await RunAsync("--unblock", "2001:db8:1:2::77");

        Assert.Equal(0, exitCode);
        Assert.Equal(0, Count());
    }

    [Fact]
    public async Task Unblock_OfAnUnknownAddress_FailsWithAHint()
    {
        var exitCode = await RunAsync("--unblock", "203.0.113.99");

        Assert.Equal(1, exitCode);
        Assert.Contains("steht nicht in der Liste", _error.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("--other")]
    public async Task Unblock_WithoutAnAddress_ShowsTheUsage(params string[] following)
    {
        var exitCode = await RunAsync(["--unblock", .. following]);

        Assert.Equal(2, exitCode);
        Assert.Contains("--unblock <Adresse>", _error.ToString());
    }

    [Fact]
    public async Task Unblocking_ByConsole_IsWrittenToTheAuditLog_AsKonsole()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(1));
        Add("203.0.113.2", 18, permanent: true);

        await RunAsync("--unblock", "203.0.113.2");
        await RunAsync("--unblock-all");

        await using var db = CreateDb();
        var entries = db.AuditEntries.OrderBy(e => e.Id).ToList();
        Assert.Equal([AuditActions.IpUnblocked, AuditActions.IpUnblockedAll], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal(BlockCommands.ConsoleActor, e.ActorName));
        Assert.Equal("203.0.113.2", entries[0].Target);
    }

    [Fact]
    public async Task UnblockAll_RemovesEverything_AndReportsTheCount()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(1));
        Add("203.0.113.2", 18, permanent: true);

        var exitCode = await RunAsync("--unblock-all");

        Assert.Equal(0, exitCode);
        Assert.Contains("2 Adressen entsperrt", _output.ToString());
        Assert.Equal(0, Count());
    }

    [Fact]
    public async Task AMissingDatabase_IsReportedWithoutCreatingOne()
    {
        var path = Path.Combine(_directory.Path, "gibtsnicht", "wunschkino.db");

        var exitCode = await BlockCommands.RunAsync(["--list-blocks"], path, _output, _error, _time);

        Assert.Equal(1, exitCode);
        Assert.Contains("Keine Datenbank", _error.ToString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AnOldDatabaseWithoutTheTable_ExplainsWhatToDo()
    {
        var oldPath = Path.Combine(_directory.Path, "alt.db");
        await using (var connection = new SqliteConnection($"Data Source={oldPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Users (Id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var exitCode = await BlockCommands.RunAsync(["--list-blocks"], oldPath, _output, _error, _time);

        Assert.Equal(1, exitCode);
        Assert.Contains("Starte die App einmal", _error.ToString());
    }
}
