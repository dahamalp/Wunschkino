using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;

namespace Wunschkino.Tests.Helpers;

/// <summary>
/// SQLite-Datei in einem Temp-Ordner statt In-Memory: Jeder Context bekommt eine eigene Verbindung,
/// so lassen sich auch parallele Zugriffe (Lockout, Last-Admin) realistisch testen.
/// </summary>
public sealed class TestDb : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDb()
    {
        var connectionString = $"Data Source={Path.Combine(_directory.Path, "test.db")};Pooling=False";
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public AppDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        _directory.Dispose();
    }
}
