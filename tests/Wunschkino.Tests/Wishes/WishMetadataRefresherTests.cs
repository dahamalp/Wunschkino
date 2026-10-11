using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;

namespace Wunschkino.Tests.Wishes;

public class WishMetadataRefresherTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Stale = WishMetadataRefresher.MaxAge + TimeSpan.FromDays(30);

    private readonly TestDb _db = new();
    private readonly SettingsService _settings;
    private readonly User _user;
    private FakeHttpHandler _tmdbHandler = TmdbReturning("""{"id":603,"title":"Matrix Neu","release_date":"1999-03-30","poster_path":"/matrix2.jpg","imdb_id":"tt0133093"}""");

    public WishMetadataRefresherTests()
    {
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _user = TestUsers.Add(_db, "alice");
    }

    public Task InitializeAsync() => _settings.SetAsync(SettingKeys.TmdbApiKey, "v3key", isProtected: true);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    private static FakeHttpHandler TmdbReturning(string json) => FakeHttpHandler.Json(HttpStatusCode.OK, json);

    private WishMetadataRefresher CreateRefresher() => new(
        _db, _settings, new TmdbClient(new HttpClient(_tmdbHandler)), TimeProvider.System, NullLogger<WishMetadataRefresher>.Instance);

    private int AddWish(int tmdbId, TimeSpan age, string? imdbId = "tt0133093")
    {
        using var db = _db.CreateDbContext();
        var fetched = DateTime.UtcNow - age;
        var wish = new Wish
        {
            TmdbId = tmdbId,
            Kind = MediaKind.Movie,
            Title = "Matrix Alt",
            Year = 1998,
            PosterPath = "/alt.jpg",
            ImdbId = imdbId,
            MetadataFetchedUtc = fetched,
            CreatedByUserId = _user.Id,
            CreatedUtc = fetched,
            UpdatedUtc = fetched,
            Votes = [new WishVote { UserId = _user.Id, CreatedUtc = fetched }]
        };
        db.Wishes.Add(wish);
        db.SaveChanges();
        return wish.Id;
    }

    private Wish Load(int id)
    {
        using var db = _db.CreateDbContext();
        return db.Wishes.Single(w => w.Id == id);
    }

    [Fact]
    public async Task RefreshStale_RenewsTheTmdbDataOfOldWishes_AndRestartsTheClock()
    {
        var id = AddWish(603, Stale);

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(1, refreshed);
        var wish = Load(id);
        Assert.Equal("Matrix Neu", wish.Title);
        Assert.Equal(1999, wish.Year);
        Assert.Equal("/matrix2.jpg", wish.PosterPath);
        Assert.True(wish.MetadataFetchedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task RefreshStale_LeavesRecentWishesAlone_WithoutAskingTmdb()
    {
        var id = AddWish(603, WishMetadataRefresher.MaxAge - TimeSpan.FromDays(10));

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(0, refreshed);
        Assert.Empty(_tmdbHandler.Requests);
        Assert.Equal("Matrix Alt", Load(id).Title);
    }

    [Fact]
    public async Task RefreshStale_DoesNotChangeStatusOrUpdateTime()
    {
        var id = AddWish(603, Stale);
        var before = Load(id);

        await CreateRefresher().RefreshStaleAsync();

        var after = Load(id);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
    }

    [Fact]
    public async Task RefreshStale_WhenTmdbFails_KeepsTheWishUntouched_ForTheNextRun()
    {
        var id = AddWish(603, Stale);
        var before = Load(id);
        _tmdbHandler = FakeHttpHandler.Throws(new HttpRequestException("TMDB down"));

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(0, refreshed);
        var after = Load(id);
        Assert.Equal("Matrix Alt", after.Title);
        Assert.Equal(before.MetadataFetchedUtc, after.MetadataFetchedUtc);
    }

    [Fact]
    public async Task RefreshStale_OneFailingWish_DoesNotBlockTheOthers()
    {
        var failing = AddWish(1001, Stale + TimeSpan.FromDays(10));
        var healthy = AddWish(1002, Stale);
        _tmdbHandler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/1001")
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":1002,"title":"Matrix Neu","release_date":"1999-03-30"}""", System.Text.Encoding.UTF8, "application/json")
            });

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(1, refreshed);
        Assert.Equal("Matrix Alt", Load(failing).Title);
        Assert.Equal("Matrix Neu", Load(healthy).Title);
    }

    [Fact]
    public async Task RefreshStale_StopsAfterThreeFailuresInARow()
    {
        for (var i = 1; i <= 6; i++)
            AddWish(1000 + i, Stale + TimeSpan.FromDays(i));
        _tmdbHandler = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests);

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(0, refreshed);
        Assert.Equal(3, _tmdbHandler.Requests.Count);
    }

    [Fact]
    public async Task RefreshStale_KeepsAnAdminDecision_EvenForNonDefaultStatus()
    {
        var id = AddWish(603, Stale);
        using (var db = _db.CreateDbContext())
        {
            var wish = db.Wishes.Single(w => w.Id == id);
            wish.Status = WishStatus.Accepted;
            wish.AdminComment = "kommt bald";
            db.SaveChanges();
        }

        await CreateRefresher().RefreshStaleAsync();

        var after = Load(id);
        Assert.Equal(WishStatus.Accepted, after.Status);
        Assert.Equal("kommt bald", after.AdminComment);
    }

    [Fact]
    public async Task RefreshStale_WhenTmdbNoLongerKnowsTheTitle_KeepsTheDataAndChecksAgainLater()
    {
        var id = AddWish(603, Stale);
        _tmdbHandler = FakeHttpHandler.Json(HttpStatusCode.NotFound);

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(0, refreshed);
        var wish = Load(id);
        Assert.Equal("Matrix Alt", wish.Title);
        Assert.True(wish.MetadataFetchedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task RefreshStale_KeepsTheKnownImdbId_WhenTmdbDeliversNone()
    {
        var id = AddWish(603, Stale, imdbId: "tt0133093");
        _tmdbHandler = TmdbReturning("""{"id":603,"title":"Matrix Neu","release_date":"1999-03-30"}""");

        await CreateRefresher().RefreshStaleAsync();

        Assert.Equal("tt0133093", Load(id).ImdbId);
    }

    [Fact]
    public async Task RefreshStale_WithoutTmdbKey_DoesNothing()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Settings.RemoveRange(db.Settings);
            db.SaveChanges();
        }
        AddWish(603, Stale);

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(0, refreshed);
        Assert.Empty(_tmdbHandler.Requests);
    }

    [Fact]
    public async Task RefreshStale_HandlesAtMost25WishesPerRun_OldestFirst()
    {
        for (var i = 1; i <= 30; i++)
            AddWish(1000 + i, Stale + TimeSpan.FromDays(i));

        var refreshed = await CreateRefresher().RefreshStaleAsync();

        Assert.Equal(25, refreshed);
        using var db = _db.CreateDbContext();
        var untouched = db.Wishes.Where(w => w.Title == "Matrix Alt").Select(w => w.TmdbId).ToList();
        Assert.Equal(5, untouched.Count);
        Assert.All(untouched, id => Assert.InRange(id, 1001, 1005));
    }
}
