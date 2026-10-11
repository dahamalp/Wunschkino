using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wunschkino.Data;
using Wunschkino.Library;

namespace Wunschkino.Tests.Helpers;

/// <summary>Prüft, dass vorhandene Datenbanken (mit echten Wünschen) die neueste Migration überleben.</summary>
public class MigrationUpgradeTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ExistingWishes_SurviveTheAutoCompletionMigration_AndStayEligible()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory.Path, "alt.db")};Pooling=False")
            .Options;

        const string beforeAutoCompletion = "20261002073232_AddWishes";
        using (var old = new AppDbContext(options))
        {
            old.GetService<IMigrator>().Migrate(beforeAutoCompletion);
            old.Database.ExecuteSqlRaw(
                "INSERT INTO Users (Username, PasswordHash, Role, IsDisabled, FailedLogins, CreatedAtUtc, SecurityStamp) " +
                "VALUES ('alt', 'x', 'User', 0, 0, '2026-10-01 10:00:00', 'stamp')");
            old.Database.ExecuteSqlRaw(
                "INSERT INTO Wishes (TmdbId, Kind, Title, Status, CreatedByUserId, CreatedUtc, UpdatedUtc) " +
                "VALUES (603, 'Movie', 'Matrix', 'Open', 1, '2026-10-01 10:00:00', '2026-10-01 10:00:00')");
        }

        using var upgraded = new AppDbContext(options);
        upgraded.Database.Migrate();

        var wish = upgraded.Wishes.Single();
        Assert.Equal("Matrix", wish.Title);
        Assert.Equal(MediaKind.Movie, wish.Kind);
        Assert.Equal(WishStatus.Open, wish.Status);
        Assert.Equal(WishAutoCompletion.None, wish.AutoCompletion);
        Assert.Equal(wish.CreatedUtc, wish.MetadataFetchedUtc);

        // Vorhandene Nutzer bleiben erhalten, und die zuletzt hinzugekommene Tabelle ist benutzbar.
        Assert.Equal("alt", upgraded.Users.Single().Username);
        upgraded.SeriesGaps.Add(new SeriesGap { JellyfinId = "s1", Title = "Serie", TmdbId = 1399, LastAttemptUtc = DateTime.UtcNow });
        upgraded.SaveChanges();
        Assert.Single(upgraded.SeriesGaps);
    }

    [Fact]
    public void ExistingUsers_GetTheSystemAppearance_WhenTheThemeColumnIsAdded()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory.Path, "theme.db")};Pooling=False")
            .Options;

        const string beforeThemePreference = "20261003232446_AddWishActions";
        using (var old = new AppDbContext(options))
        {
            old.GetService<IMigrator>().Migrate(beforeThemePreference);
            old.Database.ExecuteSqlRaw(
                "INSERT INTO Users (Username, PasswordHash, Role, IsDisabled, FailedLogins, CreatedAtUtc, SecurityStamp) " +
                "VALUES ('alt', 'x', 'Admin', 0, 0, '2026-10-01 10:00:00', 'stamp')");
        }

        using var upgraded = new AppDbContext(options);
        upgraded.Database.Migrate();

        var user = upgraded.Users.Single();
        Assert.Equal("alt", user.Username);
        Assert.Equal(ThemePreference.System, user.ThemePreference);
        Assert.Equal("stamp", user.SecurityStamp);
    }

    [Fact]
    public void TheModelHasNoPendingChanges_ComparedToTheMigrations()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory.Path, "model.db")};Pooling=False")
            .Options;
        using var db = new AppDbContext(options);

        Assert.False(db.GetService<IMigrationsAssembly>().ModelSnapshot is null);
        Assert.False(db.Database.HasPendingModelChanges(), "Das Modell weicht von den Migrationen ab: Fehlt eine Migration?");
    }
}
