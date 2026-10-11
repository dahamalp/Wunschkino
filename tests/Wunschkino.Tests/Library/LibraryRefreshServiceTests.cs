using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Gaps;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;
using static Wunschkino.Tests.Helpers.JellyfinFixtures;

namespace Wunschkino.Tests.Library;

public class LibraryRefreshServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly LibraryCache _cache = new();
    private Func<HttpResponseMessage> _jellyfinResponse = () => new HttpResponseMessage(HttpStatusCode.OK);
    private readonly ServiceProvider _services;
    private readonly LibraryRefreshService _refresh;

    public LibraryRefreshServiceTests()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IDbContextFactory<AppDbContext>>(_db);
        collection.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        collection.AddSingleton<SettingsService>();
        collection.AddSingleton(new Wunschkino.Auth.UserService(_db, new PlainTextHasher(), TimeProvider.System));
        collection.AddScoped<ConnectionSettingsService>();
        collection.AddScoped(_ => new JellyfinClient(new HttpClient(new FakeHttpHandler(_ => _jellyfinResponse()))));
        collection.AddScoped(_ => new TmdbClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, """{"id":603,"title":"Matrix Neu","release_date":"1999-03-30"}"""))));
        collection.AddSingleton(TimeProvider.System);
        collection.AddScoped<WishMetadataRefresher>();
        collection.AddSingleton<AnimeIdMapping>();
        collection.AddScoped<SeriesGapService>();
        collection.AddSingleton<SeriesGapProgress>();
        _services = collection.BuildServiceProvider();
        _refresh = CreateRefresh(new WishAutoCompleter(_db, new AnimeIdMapping(), TimeProvider.System, NullLogger<WishAutoCompleter>.Instance));
    }

    private LibraryRefreshService CreateRefresh(WishAutoCompleter completer) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _cache, completer, TimeProvider.System, NullLogger<LibraryRefreshService>.Instance);

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task WithoutJellyfinSettings_ReportsNotConfigured()
    {
        await _refresh.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal("Jellyfin ist noch nicht eingerichtet.", _cache.Snapshot.Error);
    }

    [Fact]
    public async Task StaleWishData_IsRefreshed_EvenWhenJellyfinIsNotConfigured()
    {
        await _services.GetRequiredService<SettingsService>().SetAsync(SettingKeys.TmdbApiKey, "v3key", isProtected: true);
        var user = TestUsers.Add(_db, "alice");
        var old = DateTime.UtcNow - WishMetadataRefresher.MaxAge - TimeSpan.FromDays(30);
        using (var db = _db.CreateDbContext())
        {
            db.Wishes.Add(new Wish
            {
                TmdbId = 603, Kind = MediaKind.Movie, Title = "Matrix Alt", MetadataFetchedUtc = old,
                CreatedByUserId = user.Id, CreatedUtc = old, UpdatedUtc = old,
                Votes = [new WishVote { UserId = user.Id, CreatedUtc = old }]
            });
            db.SaveChanges();
        }

        await _refresh.RefreshOnceAsync(CancellationToken.None);

        using var check = _db.CreateDbContext();
        Assert.Equal("Matrix Neu", check.Wishes.Single().Title);
    }

    [Fact]
    public async Task Success_AlsoChecksTheSeriesForMissingEpisodes()
    {
        await ConfigureAsync();
        await _services.GetRequiredService<SettingsService>().SetAsync(SettingKeys.TmdbApiKey, "v3key", isProtected: true);
        _jellyfinResponse = () => Json(ItemsJson(1, Series("s1", "Serie", tmdb: "1399")));

        await _refresh.RefreshOnceAsync(CancellationToken.None);

        using var db = _db.CreateDbContext();
        var gap = Assert.Single(db.SeriesGaps);
        Assert.Equal("s1", gap.JellyfinId);
        Assert.True(gap.HasResult);
    }

    [Fact]
    public async Task Success_WithoutTmdbKey_SkipsTheEpisodeCheck_AndStillFillsTheCache()
    {
        await ConfigureAsync();
        _jellyfinResponse = () => Json(ItemsJson(1, Series("s1", "Serie", tmdb: "1399")));

        await _refresh.RefreshOnceAsync(CancellationToken.None);

        using var db = _db.CreateDbContext();
        Assert.Empty(db.SeriesGaps);
        Assert.Null(_cache.Snapshot.Error);
        Assert.Single(_cache.Snapshot.Items);
    }

    [Fact]
    public async Task Success_FillsCacheAndUsesConfiguredInterval()
    {
        await ConfigureAsync(refreshMinutes: "30");
        _jellyfinResponse = () => Json(ItemsJson(1, Movie("m1", "Beispielfilm", tmdb: "603")));

        var interval = await _refresh.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(30), interval);
        Assert.Null(_cache.Snapshot.Error);
        Assert.Equal("m1", _cache.FindByTmdb(MediaKind.Movie, "603")?.JellyfinId);
    }

    [Fact]
    public async Task Failure_KeepsPreviousItemsAndReportsError()
    {
        await ConfigureAsync();
        _jellyfinResponse = () => Json(ItemsJson(1, Movie("m1", "Beispielfilm")));
        await _refresh.RefreshOnceAsync(CancellationToken.None);

        _jellyfinResponse = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await _refresh.RefreshOnceAsync(CancellationToken.None);

        Assert.Single(_cache.Snapshot.Items);
        Assert.Contains("abgelehnt", _cache.Snapshot.Error);
    }

    [Fact]
    public async Task Success_CompletesWishesWhoseTitleAppearedInJellyfin()
    {
        await ConfigureAsync();
        var user = TestUsers.Add(_db, "alice");
        using (var db = _db.CreateDbContext())
        {
            db.Wishes.Add(new Wish
            {
                TmdbId = 603, Kind = MediaKind.Movie, Title = "Matrix", CreatedByUserId = user.Id,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                Votes = [new WishVote { UserId = user.Id, CreatedUtc = DateTime.UtcNow }]
            });
            db.SaveChanges();
        }
        _jellyfinResponse = () => Json(ItemsJson(1, Movie("m1", "Matrix", tmdb: "603")));

        await _refresh.RefreshOnceAsync(CancellationToken.None);

        using var check = _db.CreateDbContext();
        var wish = check.Wishes.Single();
        Assert.Equal(WishStatus.Done, wish.Status);
        Assert.Equal(WishAutoCompletion.Completed, wish.AutoCompletion);
    }

    [Fact]
    public async Task FailedRefresh_DoesNotTouchWishes()
    {
        await ConfigureAsync();
        var user = TestUsers.Add(_db, "alice");
        using (var db = _db.CreateDbContext())
        {
            db.Wishes.Add(new Wish
            {
                TmdbId = 603, Kind = MediaKind.Movie, Title = "Matrix", CreatedByUserId = user.Id,
                CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                Votes = [new WishVote { UserId = user.Id, CreatedUtc = DateTime.UtcNow }]
            });
            db.SaveChanges();
        }
        _jellyfinResponse = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        await _refresh.RefreshOnceAsync(CancellationToken.None);

        using var check = _db.CreateDbContext();
        Assert.Equal(WishStatus.Open, check.Wishes.Single().Status);
    }

    [Fact]
    public async Task WishCompletionFailure_DoesNotBreakTheLibraryRefresh()
    {
        await ConfigureAsync();
        var refresh = CreateRefresh(new WishAutoCompleter(new ThrowingDbFactory(), new AnimeIdMapping(), TimeProvider.System, NullLogger<WishAutoCompleter>.Instance));
        _jellyfinResponse = () => Json(ItemsJson(1, Movie("m1", "Matrix", tmdb: "603")));

        await refresh.RefreshOnceAsync(CancellationToken.None);

        Assert.Null(_cache.Snapshot.Error);
        Assert.Single(_cache.Snapshot.Items);
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("Datenbank nicht erreichbar");
    }

    private async Task ConfigureAsync(string? refreshMinutes = null)
    {
        var settings = _services.GetRequiredService<SettingsService>();
        await settings.SetAsync(SettingKeys.JellyfinUrl, "http://jellyfin:8096", isProtected: false);
        await settings.SetAsync(SettingKeys.JellyfinApiKey, "key", isProtected: true);
        if (refreshMinutes is not null)
            await settings.SetAsync(SettingKeys.JellyfinRefreshMinutes, refreshMinutes, isProtected: false);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
