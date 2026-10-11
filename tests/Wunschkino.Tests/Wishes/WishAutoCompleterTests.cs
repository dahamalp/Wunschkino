using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;

namespace Wunschkino.Tests.Wishes;

public class WishAutoCompleterTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AnimeIdMapping _mapping = new();
    private readonly WishAutoCompleter _completer;
    private readonly User _user;

    public WishAutoCompleterTests()
    {
        _completer = new WishAutoCompleter(_db, _mapping, TimeProvider.System, NullLogger<WishAutoCompleter>.Instance);
        _user = TestUsers.Add(_db, "alice");
    }

    public void Dispose() => _db.Dispose();

    private int AddWish(int tmdbId, MediaKind kind = MediaKind.Movie, WishStatus status = WishStatus.Open, string? imdbId = null, string? comment = null, bool suppressed = false)
    {
        using var db = _db.CreateDbContext();
        var wish = new Wish
        {
            TmdbId = tmdbId,
            Kind = kind,
            Title = $"Titel {tmdbId}",
            ImdbId = imdbId,
            Status = status,
            AdminComment = comment,
            AutoCompletion = suppressed ? WishAutoCompletion.Suppressed : WishAutoCompletion.None,
            CreatedByUserId = _user.Id,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedUtc = DateTime.UtcNow.AddDays(-1),
            Votes = [new WishVote { UserId = _user.Id, CreatedUtc = DateTime.UtcNow.AddDays(-1) }]
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

    private static LibraryItem InLibrary(string tmdb, MediaKind kind = MediaKind.Movie, string? imdb = null) =>
        JellyfinFixtures.LibraryItem($"j{tmdb}", "Titel", kind, tmdb: tmdb, imdb: imdb);

    [Theory]
    [InlineData(WishStatus.Open)]
    [InlineData(WishStatus.Accepted)]
    public async Task Complete_SetsOpenAndAcceptedWishesToDone_AndMarksThemAsAutomatic(WishStatus status)
    {
        var id = AddWish(603, status: status);

        var completed = await _completer.CompleteAsync([InLibrary("603")]);

        Assert.Equal(1, completed);
        var wish = Load(id);
        Assert.Equal(WishStatus.Done, wish.Status);
        Assert.Equal(WishAutoCompletion.Completed, wish.AutoCompletion);
        Assert.True(wish.UpdatedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Theory]
    [InlineData(WishStatus.Rejected)]
    [InlineData(WishStatus.Done)]
    public async Task Complete_LeavesRejectedAndDoneWishesAlone(WishStatus status)
    {
        var id = AddWish(603, status: status);

        var completed = await _completer.CompleteAsync([InLibrary("603")]);

        Assert.Equal(0, completed);
        var wish = Load(id);
        Assert.Equal(status, wish.Status);
        Assert.Equal(WishAutoCompletion.None, wish.AutoCompletion);
    }

    [Fact]
    public async Task Complete_DoesNotMixUpMoviesAndSeriesWithTheSameTmdbId()
    {
        var series = AddWish(1399, MediaKind.Series);
        var movie = AddWish(1399, MediaKind.Movie);

        await _completer.CompleteAsync([InLibrary("1399", MediaKind.Series)]);

        Assert.Equal(WishStatus.Done, Load(series).Status);
        Assert.Equal(WishStatus.Open, Load(movie).Status);
    }

    [Fact]
    public async Task Complete_MatchesViaImdbIdWhenJellyfinHasNoTmdbId()
    {
        var id = AddWish(603, imdbId: "tt0133093");

        await _completer.CompleteAsync([JellyfinFixtures.LibraryItem("j1", "Matrix", imdb: "tt0133093")]);

        Assert.Equal(WishStatus.Done, Load(id).Status);
    }

    [Fact]
    public async Task Complete_MatchesAnimeViaTheAniListPluginIdAndTheMappingList()
    {
        _mapping.Replace(new Dictionary<int, AnimeMapping> { [9989] = new(9989, MediaKind.Series, 1399, []) }, DateTime.UtcNow);
        var id = AddWish(1399, MediaKind.Series);
        var anime = new LibraryItem("j1", "Anime", MediaKind.Series, null, null, true, new Dictionary<string, string> { ["AniList"] = "9989" });

        var completed = await _completer.CompleteAsync([anime]);

        Assert.Equal(1, completed);
        Assert.Equal(WishStatus.Done, Load(id).Status);
    }

    [Fact]
    public async Task Complete_SkipsWishesAnAdminHasReopened()
    {
        var id = AddWish(603, status: WishStatus.Open, suppressed: true);

        var completed = await _completer.CompleteAsync([InLibrary("603")]);

        Assert.Equal(0, completed);
        var wish = Load(id);
        Assert.Equal(WishStatus.Open, wish.Status);
        Assert.Equal(WishAutoCompletion.Suppressed, wish.AutoCompletion);
    }

    [Fact]
    public async Task Complete_MatchesAnimeMappedOnlyToImdbIdsViaTheWishImdbId()
    {
        _mapping.Replace(new Dictionary<int, AnimeMapping> { [888] = new(888, null, null, ["tt0000888"]) }, DateTime.UtcNow);
        var id = AddWish(777, MediaKind.Series, imdbId: "tt0000888");
        var anime = new LibraryItem("j1", "Anime", MediaKind.Series, null, null, true, new Dictionary<string, string> { ["AniList"] = "888" });

        await _completer.CompleteAsync([anime]);

        Assert.Equal(WishStatus.Done, Load(id).Status);
    }

    [Fact]
    public async Task Complete_WithAnEmptyLibrary_ChangesNothing()
    {
        var id = AddWish(603);

        var completed = await _completer.CompleteAsync([]);

        Assert.Equal(0, completed);
        Assert.Equal(WishStatus.Open, Load(id).Status);
    }

    [Fact]
    public async Task Complete_WithoutMatches_ChangesNothing()
    {
        var id = AddWish(603);

        Assert.Equal(0, await _completer.CompleteAsync([InLibrary("999")]));
        Assert.Equal(WishStatus.Open, Load(id).Status);
    }

    [Fact]
    public async Task Complete_KeepsTheAdminComment()
    {
        var id = AddWish(603, status: WishStatus.Accepted, comment: "kommt im Herbst");

        await _completer.CompleteAsync([InLibrary("603")]);

        Assert.Equal("kommt im Herbst", Load(id).AdminComment);
    }

    [Fact]
    public async Task Complete_SecondRunDoesNotCountAlreadyCompletedWishes()
    {
        AddWish(603);
        await _completer.CompleteAsync([InLibrary("603")]);

        Assert.Equal(0, await _completer.CompleteAsync([InLibrary("603")]));
    }

    [Fact]
    public async Task Complete_HandlesMoreWishesThanOneBatch()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Wishes.AddRange(Enumerable.Range(1, 1200).Select(i => new Wish
            {
                TmdbId = i,
                Kind = MediaKind.Movie,
                Title = $"Titel {i}",
                CreatedByUserId = _user.Id,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Votes = [new WishVote { UserId = _user.Id, CreatedUtc = DateTime.UtcNow }]
            }));
            db.SaveChanges();
        }
        var library = Enumerable.Range(1, 1200).Select(i => InLibrary(i.ToString())).ToList();

        var completed = await _completer.CompleteAsync(library);

        Assert.Equal(1200, completed);
        using var check = _db.CreateDbContext();
        Assert.All(check.Wishes, w => Assert.Equal(WishStatus.Done, w.Status));
    }
}
