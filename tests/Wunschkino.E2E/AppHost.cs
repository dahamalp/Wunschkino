using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Setup;

namespace Wunschkino.E2E;

/// <summary>Startet die echte App auf einem Kestrel-Port, damit ein Browser sie erreicht; eigener Datenordner je Instanz.</summary>
public sealed class AppHost : WebApplicationFactory<Program>
{
    public const string AdminName = "admin";
    public const string UserName = "nutzer";
    public const string Password = "ein-sicheres-passwort";

    private readonly string _dataDirectory = Directory.CreateTempSubdirectory("wunschkino-e2e-").FullName;
    private readonly bool _seed;

    public AppHost(bool seed)
    {
        _seed = seed;
        UseKestrel(0);
    }

    public string BaseAddress => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseStaticWebAssets();
        builder.UseSetting("Wunschkino:DataDirectory", _dataDirectory);
        builder.UseSetting("Wunschkino:SqlitePooling", "false");
        builder.UseSetting("Wunschkino:SecureCookies", "false");
    }

    public async Task StartAsync()
    {
        StartServer();
        if (!_seed)
            return;

        var dbFactory = Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var hasher = Services.GetRequiredService<IPasswordHasher>();
        db.Users.Add(new User { Username = AdminName, PasswordHash = hasher.Hash(Password), Role = UserRole.Admin, CreatedAtUtc = DateTime.UtcNow });
        db.Users.Add(new User { Username = UserName, PasswordHash = hasher.Hash(Password), Role = UserRole.User, CreatedAtUtc = DateTime.UtcNow.AddDays(-3) });
        await db.SaveChangesAsync();
        Services.GetRequiredService<SetupState>().MarkCompleted();
    }


    /// <summary>Verbindet die App mit dem Fake-Jellyfin, wartet auf den Bestand und legt Demo-Wünsche an.</summary>
    public async Task SeedDemoAsync(FakeJellyfin jellyfin)
    {
        var settings = Services.GetRequiredService<SettingsService>();
        await settings.SetAsync(SettingKeys.JellyfinUrl, jellyfin.Url, isProtected: false);
        await settings.SetAsync(SettingKeys.JellyfinApiKey, FakeJellyfin.ApiKey, isProtected: true);
        await settings.SetAsync(SettingKeys.TmdbApiKey, "demo-tmdb-key-0123456789", isProtected: true);

        var dbFactory = Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var userId = await db.Users.Where(u => u.Username == UserName).Select(u => u.Id).SingleAsync();
            var adminId = await db.Users.Where(u => u.Username == AdminName).Select(u => u.Id).SingleAsync();
            var now = DateTime.UtcNow;
            (string Title, int Year, MediaKind Kind, WishStatus Status, int Owner, string? Comment)[] wishes =
            [
                ("Das Geheimnis der blauen Schlucht", 2024, MediaKind.Movie, WishStatus.Open, userId, null),
                ("Aschenputtel im Weltraum", 2023, MediaKind.Movie, WishStatus.Accepted, adminId, "Wird besorgt."),
                ("Der Gärtner von Babylon", 2019, MediaKind.Series, WishStatus.Done, userId, null),
                ("Ein sehr langer Wunschtitel, der in einer Karte auf dem Handy umbrechen muss", 2021, MediaKind.Series, WishStatus.Open, adminId, null),
                ("Nicht verfügbar", 2010, MediaKind.Movie, WishStatus.Rejected, userId, "Leider nicht zu bekommen.")
            ];
            for (var i = 0; i < wishes.Length; i++)
            {
                var w = wishes[i];
                db.Wishes.Add(new Wish
                {
                    TmdbId = 9000 + i,
                    Kind = w.Kind,
                    Title = w.Title,
                    Year = w.Year,
                    MetadataFetchedUtc = now,
                    Status = w.Status,
                    AdminComment = w.Comment,
                    CreatedByUserId = w.Owner,
                    CreatedUtc = now.AddDays(-i),
                    UpdatedUtc = now.AddDays(-i),
                    Votes = [new WishVote { UserId = w.Owner, CreatedUtc = now.AddDays(-i) }]
                });
            }
            await db.SaveChangesAsync();
        }

        Services.GetServices<IHostedService>().OfType<LibraryRefreshService>().Single().RequestRefresh();
        var cache = Services.GetRequiredService<LibraryCache>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (cache.Snapshot.Items.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        if (cache.Snapshot.Items.Count == 0)
            throw new InvalidOperationException($"Der Demo-Bestand wurde nicht geladen: {cache.Snapshot.Error}");
    }

    public async Task AddOpenWishAsync(int tmdbId, MediaKind kind, string title, int year)
    {
        var dbFactory = Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var userId = await db.Users.Where(u => u.Username == UserName).Select(u => u.Id).SingleAsync();
        var now = DateTime.UtcNow;
        db.Wishes.Add(new Wish
        {
            TmdbId = tmdbId,
            Kind = kind,
            Title = title,
            Year = year,
            MetadataFetchedUtc = now,
            Status = WishStatus.Open,
            CreatedByUserId = userId,
            CreatedUtc = now,
            UpdatedUtc = now,
            Votes = [new WishVote { UserId = userId, CreatedUtc = now }]
        });
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
    }
}
