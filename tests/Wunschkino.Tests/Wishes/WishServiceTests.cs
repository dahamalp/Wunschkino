using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Anime;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;

namespace Wunschkino.Tests.Wishes;

public class WishServiceTests : IAsyncLifetime, IDisposable
{
    private const int MatrixId = 603;
    private const int ThronesId = 1399;

    private readonly TestDb _db = new();
    private readonly LibraryCache _library = new();
    private readonly StubTmdbHandler _tmdbHandler = new();
    private readonly SettingsService _settings;
    private readonly WishService _service;
    private readonly User _alice;
    private readonly User _bob;
    private readonly User _admin;

    public WishServiceTests()
    {
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _service = CreateService(FakeHttpHandler.Json(HttpStatusCode.OK, AniListSearchJson));
        _alice = TestUsers.Add(_db, "alice");
        _bob = TestUsers.Add(_db, "bob");
        _admin = TestUsers.Add(_db, "admin", UserRole.Admin);
    }

    private const string AniListSearchJson = """
        {"data":{"Page":{"media":[
          {"id":9989,"format":"TV","seasonYear":2011,"startDate":{"year":2011},"title":{"romaji":"Ano Hi Mita Hana no Namae wo Bokutachi wa Mada Shiranai.","english":"Anohana","native":"x"},"coverImage":{"large":"https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx9989-hImMg6kCMm6I.jpg"}},
          {"id":15039,"format":"MOVIE","startDate":{"year":2013},"title":{"romaji":"Anohana Movie","english":null,"native":null},"coverImage":{"large":"https://evil.example/x.jpg"}},
          {"id":777,"format":"OVA","startDate":{"year":2010},"title":{"romaji":"Ohne Zuordnung","english":null,"native":null},"coverImage":{"large":null}},
          {"id":888,"format":"TV","startDate":{"year":2015},"title":{"romaji":"Schon da","english":null,"native":null},"coverImage":{"large":null}}
        ]}}}
        """;

    private readonly AnimeIdMapping _animeMapping = new();

    private WishService CreateService(HttpMessageHandler aniListHandler, UserSearchThrottle? searchThrottle = null) => new(
        _db,
        _settings,
        new TmdbClient(new HttpClient(_tmdbHandler)),
        new AniListClient(new HttpClient(aniListHandler), new MemoryCache(new MemoryCacheOptions()), new AniListThrottle(TimeProvider.System)),
        _animeMapping,
        _library,
        TimeProvider.System,
        searchThrottle);

    private void SeedAnimeMapping() => _animeMapping.Replace(new Dictionary<int, AnimeMapping>
    {
        [9989] = new(9989, MediaKind.Series, ThronesId, []),
        [15039] = new(15039, MediaKind.Movie, MatrixId, ["tt0133093"]),
        [888] = new(888, null, null, ["tt0000888"])
    }, DateTime.UtcNow);

    public Task InitializeAsync() => _settings.SetAsync(SettingKeys.TmdbApiKey, "v3key", isProtected: true);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateOrJoin_NewTitle_StoresServerSideMetadataAndFirstVote()
    {
        var result = await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, "  Bitte  ");

        Assert.True(result.Success, result.Message);
        using var db = _db.CreateDbContext();
        var wish = db.Wishes.Single();
        Assert.Equal("Matrix", wish.Title);
        Assert.Equal(1999, wish.Year);
        Assert.Equal("/matrix1.jpg", wish.PosterPath);
        Assert.Equal("tt0133093", wish.ImdbId);
        Assert.Equal(WishStatus.Open, wish.Status);
        Assert.Equal(_alice.Id, wish.CreatedByUserId);
        var vote = db.WishVotes.Single();
        Assert.Equal(_alice.Id, vote.UserId);
        Assert.Equal("Bitte", vote.Note);
    }

    [Fact]
    public async Task CreateOrJoin_ExistingWish_AddsVoteInsteadOfSecondWish()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        var result = await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, "ich auch");

        Assert.True(result.Success, result.Message);
        using var db = _db.CreateDbContext();
        Assert.Single(db.Wishes);
        Assert.Equal(2, db.WishVotes.Count());
        // Der zweite Nutzer braucht keine neuen Metadaten.
        Assert.Single(_tmdbHandler.DetailRequests(MatrixId));
    }

    [Fact]
    public async Task CreateOrJoin_SameMovieAndSeriesIdAreDifferentTitles()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        _tmdbHandler.SetSeries(MatrixId, "Andere Serie");

        var result = await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Series, null);

        Assert.True(result.Success, result.Message);
        using var db = _db.CreateDbContext();
        Assert.Equal(2, db.Wishes.Count());
    }

    [Fact]
    public async Task CreateOrJoin_SameUserTwice_IsRejected()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        var result = await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        Assert.False(result.Success);
        using var db = _db.CreateDbContext();
        Assert.Single(db.WishVotes);
    }

    [Fact]
    public async Task CreateOrJoin_ConcurrentWishesForTheSameTitle_YieldOneWishWithAllVotes()
    {
        var users = Enumerable.Range(0, 4).Select(i => TestUsers.Add(_db, $"gast{i}")).ToList();

        var results = await Task.WhenAll(users.Select(u => _service.CreateOrJoinAsync(u.Id, MatrixId, MediaKind.Movie, null)));

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        using var db = _db.CreateDbContext();
        Assert.Single(db.Wishes);
        Assert.Equal(users.Count, db.WishVotes.Count());
    }

    [Fact]
    public async Task CreateOrJoin_TitleAlreadyInJellyfin_IsRejected()
    {
        _library.Update([JellyfinFixtures.LibraryItem("j1", "Matrix", tmdb: "603")], DateTime.UtcNow);

        var result = await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        Assert.False(result.Success);
        Assert.Contains("Jellyfin", result.Message);
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
    }

    [Fact]
    public async Task CreateOrJoin_TitleInJellyfinOnlyWithImdbId_IsRejectedAfterLookup()
    {
        _library.Update([JellyfinFixtures.LibraryItem("j1", "Matrix", imdb: "tt0133093")], DateTime.UtcNow);

        var result = await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        Assert.False(result.Success);
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
    }

    [Theory]
    [InlineData(WishStatus.Rejected, "abgelehnt")]
    [InlineData(WishStatus.Done, "erledigt")]
    public async Task CreateOrJoin_RejectedOrDoneWish_CannotBeJoined(WishStatus status, string expectedFragment)
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.SetStatusAsync(_admin.Id, WishId(), status, null);

        var result = await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, null);

        Assert.False(result.Success);
        Assert.Contains(expectedFragment, result.Message);
    }

    [Fact]
    public async Task CreateOrJoin_AcceptedWish_CanStillBeJoined()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.SetStatusAsync(_admin.Id, WishId(), WishStatus.Accepted, null);

        var result = await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, null);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task CreateOrJoin_ValidatesInput()
    {
        Assert.False((await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, new string('x', WishValidation.MaxNoteLength + 1))).Success);
        Assert.False((await _service.CreateOrJoinAsync(_alice.Id, 0, MediaKind.Movie, null)).Success);
        Assert.False((await _service.CreateOrJoinAsync(_alice.Id, MatrixId, (MediaKind)42, null)).Success);

        var disabled = TestUsers.Add(_db, "gesperrt", disabled: true);
        Assert.False((await _service.CreateOrJoinAsync(disabled.Id, MatrixId, MediaKind.Movie, null)).Success);
        Assert.False((await _service.CreateOrJoinAsync(9999, MatrixId, MediaKind.Movie, null)).Success);

        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
    }

    [Fact]
    public async Task CreateOrJoin_UnknownTmdbTitle_Fails()
    {
        var result = await _service.CreateOrJoinAsync(_alice.Id, 424242, MediaKind.Movie, null);

        Assert.False(result.Success);
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
    }

    [Fact]
    public async Task CreateOrJoin_TmdbDown_FailsWithMessageAndStoresNothing()
    {
        _tmdbHandler.Fail = true;

        var result = await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        Assert.False(result.Success);
        Assert.Contains("TMDB", result.Message);
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
    }

    [Fact]
    public async Task Withdraw_OnlyVoteOfOpenWish_RemovesTheWish()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        var result = await _service.WithdrawAsync(_alice.Id, WishId());

        Assert.True(result.Success, result.Message);
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
        Assert.Empty(db.WishVotes);
    }

    [Fact]
    public async Task Withdraw_WithOtherVotes_RemovesOnlyOwnVote()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, null);

        var result = await _service.WithdrawAsync(_alice.Id, WishId());

        Assert.True(result.Success, result.Message);
        using var db = _db.CreateDbContext();
        Assert.Single(db.Wishes);
        Assert.Equal(_bob.Id, db.WishVotes.Single().UserId);
    }

    [Fact]
    public async Task Withdraw_OnlyVoteOfAcceptedWish_IsRefused()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.SetStatusAsync(_admin.Id, WishId(), WishStatus.Accepted, null);

        var result = await _service.WithdrawAsync(_alice.Id, WishId());

        Assert.False(result.Success);
        using var db = _db.CreateDbContext();
        Assert.Single(db.Wishes);
    }

    [Fact]
    public async Task Withdraw_ByNonVoter_IsRefused()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        var result = await _service.WithdrawAsync(_bob.Id, WishId());

        Assert.False(result.Success);
        using var db = _db.CreateDbContext();
        Assert.Single(db.Wishes);
    }

    [Theory]
    [InlineData(WishStatus.Rejected)]
    [InlineData(WishStatus.Done)]
    public async Task Withdraw_RejectedOrDoneWish_IsRefused(WishStatus status)
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.SetStatusAsync(_admin.Id, WishId(), status, null);

        Assert.False((await _service.WithdrawAsync(_alice.Id, WishId())).Success);
        using var db = _db.CreateDbContext();
        Assert.Single(db.WishVotes);
    }

    [Fact]
    public async Task Withdraw_ByDisabledUser_IsRefused()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        using (var db = _db.CreateDbContext())
        {
            db.Users.Single(u => u.Id == _alice.Id).IsDisabled = true;
            db.SaveChanges();
        }

        Assert.False((await _service.WithdrawAsync(_alice.Id, WishId())).Success);
    }

    [Fact]
    public async Task Withdraw_BothVotersAtTheSameTime_LeavesNoWishWithoutVotes()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, null);
        var wishId = WishId();

        var results = await Task.WhenAll(_service.WithdrawAsync(_alice.Id, wishId), _service.WithdrawAsync(_bob.Id, wishId));

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
        Assert.Empty(db.WishVotes);
    }

    [Fact]
    public async Task SetStatus_AsAdmin_StoresStatusAndComment()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        var result = await _service.SetStatusAsync(_admin.Id, WishId(), WishStatus.Accepted, "  kommt bald ");

        Assert.True(result.Success, result.Message);
        using var db = _db.CreateDbContext();
        var wish = db.Wishes.Single();
        Assert.Equal(WishStatus.Accepted, wish.Status);
        Assert.Equal("kommt bald", wish.AdminComment);
    }

    private async Task<int> CreateAutoCompletedWishAsync()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        using var db = _db.CreateDbContext();
        var wish = db.Wishes.Single();
        wish.Status = WishStatus.Done;
        wish.AutoCompletion = WishAutoCompletion.Completed;
        db.SaveChanges();
        return wish.Id;
    }

    private WishAutoCompletion AutoCompletionOf(int wishId)
    {
        using var db = _db.CreateDbContext();
        return db.Wishes.Single(w => w.Id == wishId).AutoCompletion;
    }

    [Fact]
    public async Task SetStatus_ReopeningADoneWish_TakesItOutOfTheAutomaticCompletion()
    {
        var id = await CreateAutoCompletedWishAsync();

        await _service.SetStatusAsync(_admin.Id, id, WishStatus.Open, null);

        Assert.Equal(WishAutoCompletion.Suppressed, AutoCompletionOf(id));
    }

    [Fact]
    public async Task SetStatus_ReopenedWish_IsNotClosedAgainByTheNextLibraryComparison()
    {
        var id = await CreateAutoCompletedWishAsync();
        await _service.SetStatusAsync(_admin.Id, id, WishStatus.Open, null);
        var completer = new WishAutoCompleter(_db, _animeMapping, TimeProvider.System, NullLogger<WishAutoCompleter>.Instance);

        var completed = await completer.CompleteAsync([JellyfinFixtures.LibraryItem("j1", "Matrix", tmdb: MatrixId.ToString())]);

        Assert.Equal(0, completed);
        using var db = _db.CreateDbContext();
        Assert.Equal(WishStatus.Open, db.Wishes.Single().Status);
    }

    [Fact]
    public async Task SetStatus_OnlyEditingTheComment_KeepsTheAutomaticMark()
    {
        var id = await CreateAutoCompletedWishAsync();

        await _service.SetStatusAsync(_admin.Id, id, WishStatus.Done, "passt so");

        Assert.Equal(WishAutoCompletion.Completed, AutoCompletionOf(id));
    }

    [Fact]
    public async Task SetStatus_FromAutoDoneToRejected_RemovesTheAutomaticMark()
    {
        var id = await CreateAutoCompletedWishAsync();

        await _service.SetStatusAsync(_admin.Id, id, WishStatus.Rejected, null);

        Assert.Equal(WishAutoCompletion.None, AutoCompletionOf(id));
    }

    [Fact]
    public async Task SetStatus_AcceptingAnOpenWish_KeepsItEligibleForTheAutomaticCompletion()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        await _service.SetStatusAsync(_admin.Id, WishId(), WishStatus.Accepted, null);

        Assert.Equal(WishAutoCompletion.None, AutoCompletionOf(WishId()));
    }

    [Theory]
    [InlineData(WishStatus.Open, WishAutoCompletion.None, WishStatus.Accepted, WishAutoCompletion.None)]
    [InlineData(WishStatus.Done, WishAutoCompletion.None, WishStatus.Open, WishAutoCompletion.Suppressed)]
    [InlineData(WishStatus.Done, WishAutoCompletion.Completed, WishStatus.Accepted, WishAutoCompletion.Suppressed)]
    [InlineData(WishStatus.Done, WishAutoCompletion.Completed, WishStatus.Rejected, WishAutoCompletion.None)]
    [InlineData(WishStatus.Done, WishAutoCompletion.Completed, WishStatus.Done, WishAutoCompletion.Completed)]
    [InlineData(WishStatus.Open, WishAutoCompletion.Suppressed, WishStatus.Accepted, WishAutoCompletion.Suppressed)]
    [InlineData(WishStatus.Rejected, WishAutoCompletion.None, WishStatus.Open, WishAutoCompletion.None)]
    public void NextAutoCompletion_FollowsTheReopenRule(WishStatus current, WishAutoCompletion completion, WishStatus next, WishAutoCompletion expected) =>
        Assert.Equal(expected, WishService.NextAutoCompletion(current, completion, next));

    [Fact]
    public async Task List_ExposesWhetherAWishWasCompletedAutomatically()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.CreateOrJoinAsync(_alice.Id, ThronesId, MediaKind.Series, null);
        using (var db = _db.CreateDbContext())
        {
            var matrix = db.Wishes.Single(w => w.TmdbId == MatrixId);
            matrix.Status = WishStatus.Done;
            matrix.AutoCompletion = WishAutoCompletion.Completed;
            db.SaveChanges();
        }

        var wishes = await _service.ListAsync(_alice.Id, WishScope.All, null, WishSort.Newest);

        Assert.True(wishes.Single(w => w.TmdbId == MatrixId).CompletedAutomatically);
        Assert.False(wishes.Single(w => w.TmdbId == ThronesId).CompletedAutomatically);
    }

    [Fact]
    public async Task SetStatus_AsNormalUser_IsRefused()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        var result = await _service.SetStatusAsync(_alice.Id, WishId(), WishStatus.Done, null);

        Assert.False(result.Success);
        using var db = _db.CreateDbContext();
        Assert.Equal(WishStatus.Open, db.Wishes.Single().Status);
    }

    [Fact]
    public async Task SetStatus_ByDisabledAdmin_IsRefused()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        var disabledAdmin = TestUsers.Add(_db, "alt-admin", UserRole.Admin, disabled: true);

        var result = await _service.SetStatusAsync(disabledAdmin.Id, WishId(), WishStatus.Done, null);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SetStatus_ValidatesStatusAndCommentLength()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        Assert.False((await _service.SetStatusAsync(_admin.Id, WishId(), (WishStatus)99, null)).Success);
        Assert.False((await _service.SetStatusAsync(_admin.Id, WishId(), WishStatus.Accepted, new string('x', WishValidation.MaxCommentLength + 1))).Success);
        Assert.False((await _service.SetStatusAsync(_admin.Id, 9999, WishStatus.Accepted, null)).Success);
    }

    [Fact]
    public async Task Delete_IsAdminOnly()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        Assert.False((await _service.DeleteAsync(_alice.Id, WishId())).Success);
        Assert.True((await _service.DeleteAsync(_admin.Id, WishId())).Success);

        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
        Assert.Empty(db.WishVotes);
    }

    [Fact]
    public async Task Search_AnnotatesLibraryWishedAndWishableTitles()
    {
        _library.Update([JellyfinFixtures.LibraryItem("j1", "Game of Thrones", MediaKind.Series, tmdb: "1399")], DateTime.UtcNow);
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);
        await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, null);
        _tmdbHandler.SetSeries(777, "Neue Serie");

        var outcome = await _service.SearchAsync(_alice.Id, "alles");

        Assert.Null(outcome.Error);
        var matrix = outcome.Results.Single(r => r.Title.TmdbId == MatrixId);
        Assert.Equal(WishAvailability.Wished, matrix.Availability);
        Assert.Equal(2, matrix.Votes);
        Assert.True(matrix.VotedByMe);
        Assert.Equal(WishStatus.Open, matrix.Status);
        Assert.Equal(WishAvailability.InLibrary, outcome.Results.Single(r => r.Title.TmdbId == ThronesId).Availability);
        Assert.Equal(WishAvailability.Wishable, outcome.Results.Single(r => r.Title.TmdbId == 777).Availability);

        var asBob = await _service.SearchAsync(_admin.Id, "alles");
        Assert.False(asBob.Results.Single(r => r.Title.TmdbId == MatrixId).VotedByMe);
    }

    [Fact]
    public async Task Search_WithImdbNumber_UsesTheFindEndpoint()
    {
        var outcome = await _service.SearchAsync(_alice.Id, " TT0133093 ");

        Assert.Equal(MatrixId, Assert.Single(outcome.Results).Title.TmdbId);
        Assert.Contains(_tmdbHandler.Paths, p => p == "/3/find/tt0133093");
        Assert.DoesNotContain(_tmdbHandler.Paths, p => p.Contains("search/multi"));
    }

    [Fact]
    public async Task Search_ReturnsErrorsInsteadOfThrowing()
    {
        Assert.Empty((await _service.SearchAsync(_alice.Id, "   ")).Results);
        Assert.NotNull((await _service.SearchAsync(_alice.Id, new string('x', WishValidation.MaxQueryLength + 1))).Error);

        _tmdbHandler.Fail = true;
        Assert.Contains("TMDB", (await _service.SearchAsync(_alice.Id, "matrix")).Error);
    }

    [Fact]
    public async Task Search_WithoutConfiguredKey_ExplainsWhatToDo()
    {
        await _settings.SetAsync(SettingKeys.TmdbApiKey, null, isProtected: true);

        var outcome = await _service.SearchAsync(_alice.Id, "matrix");

        Assert.Contains("Admin", outcome.Error);
        Assert.Empty(_tmdbHandler.Paths);
    }

    [Fact]
    public async Task List_FiltersByScopeAndStatus_AndIncludesNotes()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, "für den Filmabend");
        await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, null);
        await _service.CreateOrJoinAsync(_bob.Id, ThronesId, MediaKind.Series, null);
        await _service.SetStatusAsync(_admin.Id, WishId(ThronesId), WishStatus.Accepted, null);

        var all = await _service.ListAsync(_alice.Id, WishScope.All, null, WishSort.MostVotes, viewerIsAdmin: true);
        var mine = await _service.ListAsync(_alice.Id, WishScope.Mine, null, WishSort.Newest);
        var accepted = await _service.ListAsync(_alice.Id, WishScope.All, WishStatus.Accepted, WishSort.Newest);

        Assert.Equal([MatrixId, ThronesId], all.Select(w => w.TmdbId));
        Assert.Equal(MatrixId, Assert.Single(mine).TmdbId);
        Assert.Equal(ThronesId, Assert.Single(accepted).TmdbId);
        var matrix = all[0];
        Assert.Equal(2, matrix.Votes);
        Assert.True(matrix.VotedByMe);
        Assert.Equal("alice", matrix.CreatedByName);
        Assert.Equal(new WishNote("alice", "für den Filmabend"), Assert.Single(matrix.Notes));
        Assert.False(all[1].VotedByMe);
    }

    private const int FirstExtraSeries = 7001;

    private async Task<WishResult> WishSeriesAsync(User user, int tmdbId)
    {
        _tmdbHandler.SetSeries(tmdbId, $"Serie {tmdbId}");
        return await _service.CreateOrJoinAsync(user.Id, tmdbId, MediaKind.Series, null);
    }

    [Fact]
    public async Task DailyLimit_RefusesTheWishThatExceedsIt_ForNewWishesAndPlusOneAlike()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "2", isProtected: false);
        Assert.True((await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null)).Success);
        Assert.True((await _service.CreateOrJoinAsync(_bob.Id, ThronesId, MediaKind.Series, null)).Success);
        Assert.True((await _service.CreateOrJoinAsync(_alice.Id, ThronesId, MediaKind.Series, null)).Success);

        var refused = await WishSeriesAsync(_alice, FirstExtraSeries);
        var refusedJoin = await _service.CreateOrJoinAsync(_alice.Id, ThronesId, MediaKind.Series, null);

        Assert.False(refused.Success);
        Assert.Contains("24 Stunden", refused.Message);
        Assert.Contains("2 Wünsche", refused.Message);
        Assert.False(refusedJoin.Success);
        Assert.Contains("24 Stunden", refusedJoin.Message);
        using var db = _db.CreateDbContext();
        Assert.DoesNotContain(db.Wishes, w => w.TmdbId == FirstExtraSeries);
    }

    [Fact]
    public async Task DailyLimit_DefaultsTo20_WhenNothingIsConfigured()
    {
        for (var i = 0; i < UserLimits.DefaultWishesPerDay; i++)
            Assert.True((await WishSeriesAsync(_alice, FirstExtraSeries + i)).Success, $"Wunsch {i + 1} muss klappen.");

        var twentyFirst = await WishSeriesAsync(_alice, FirstExtraSeries + UserLimits.DefaultWishesPerDay);

        Assert.False(twentyFirst.Success);
        Assert.Contains("20 Wünsche", twentyFirst.Message);
    }

    [Fact]
    public async Task DailyLimit_IsCountedPerUser()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "1", isProtected: false);
        Assert.True((await WishSeriesAsync(_alice, FirstExtraSeries)).Success);

        Assert.False((await WishSeriesAsync(_alice, FirstExtraSeries + 1)).Success);
        Assert.True((await WishSeriesAsync(_bob, FirstExtraSeries + 2)).Success);
    }

    [Fact]
    public async Task DailyLimit_AdminsAreExempt()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "1", isProtected: false);

        Assert.True((await WishSeriesAsync(_admin, FirstExtraSeries)).Success);
        Assert.True((await WishSeriesAsync(_admin, FirstExtraSeries + 1)).Success);
        Assert.True((await WishSeriesAsync(_admin, FirstExtraSeries + 2)).Success);
    }

    [Fact]
    public async Task DailyLimit_Zero_MeansNoLimit()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "0", isProtected: false);

        for (var i = 0; i < UserLimits.DefaultWishesPerDay + 5; i++)
            Assert.True((await WishSeriesAsync(_alice, FirstExtraSeries + i)).Success);
    }

    [Fact]
    public async Task DailyLimit_OnlyCountsTheLast24Hours()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "1", isProtected: false);
        Assert.True((await WishSeriesAsync(_alice, FirstExtraSeries)).Success);
        Assert.False((await WishSeriesAsync(_alice, FirstExtraSeries + 1)).Success);

        using (var db = _db.CreateDbContext())
        {
            foreach (var action in db.WishActions.Where(a => a.UserId == _alice.Id))
                action.TimeUtc = DateTime.UtcNow.AddHours(-25);
            db.SaveChanges();
        }

        Assert.True((await WishSeriesAsync(_alice, FirstExtraSeries + 1)).Success);
    }

    [Fact]
    public async Task DailyLimit_CannotBeCircumvented_ByWithdrawingAndWishingAgain()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "2", isProtected: false);
        Assert.True((await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, null)).Success);
        Assert.True((await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null)).Success);
        Assert.True((await _service.WithdrawAsync(_alice.Id, WishId(MatrixId))).Success);
        Assert.True((await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null)).Success);
        Assert.True((await _service.WithdrawAsync(_alice.Id, WishId(MatrixId))).Success);

        var third = await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        Assert.False(third.Success);
        Assert.Contains("24 Stunden", third.Message);
    }

    [Fact]
    public async Task CreatingWishes_IsThrottledLikeSearches_BeforeAskingTmdbForDetails()
    {
        await _settings.SetAsync(SettingKeys.TmdbSearchesPerMinute, "2", isProtected: false);
        await _settings.SetAsync(SettingKeys.WishesPerDay, "0", isProtected: false);
        var service = CreateService(FakeHttpHandler.Json(HttpStatusCode.OK, AniListSearchJson), new UserSearchThrottle(TimeProvider.System));
        _tmdbHandler.SetSeries(FirstExtraSeries, "A");
        _tmdbHandler.SetSeries(FirstExtraSeries + 1, "B");
        _tmdbHandler.SetSeries(FirstExtraSeries + 2, "C");

        Assert.True((await service.CreateOrJoinAsync(_alice.Id, FirstExtraSeries, MediaKind.Series, null)).Success);
        Assert.True((await service.CreateOrJoinAsync(_alice.Id, FirstExtraSeries + 1, MediaKind.Series, null)).Success);
        var requestsBefore = _tmdbHandler.Paths.Count;
        var third = await service.CreateOrJoinAsync(_alice.Id, FirstExtraSeries + 2, MediaKind.Series, null);

        Assert.False(third.Success);
        Assert.Contains("zu schnell", third.Message);
        Assert.Equal(requestsBefore, _tmdbHandler.Paths.Count);
        Assert.True((await service.CreateOrJoinAsync(_bob.Id, FirstExtraSeries + 2, MediaKind.Series, null)).Success);
    }

    [Fact]
    public async Task DailyLimit_ChangedByTheAdmin_AppliesImmediately()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "1", isProtected: false);
        Assert.True((await WishSeriesAsync(_alice, FirstExtraSeries)).Success);
        Assert.False((await WishSeriesAsync(_alice, FirstExtraSeries + 1)).Success);

        await _settings.SetAsync(SettingKeys.WishesPerDay, "3", isProtected: false);

        Assert.True((await WishSeriesAsync(_alice, FirstExtraSeries + 1)).Success);
    }

    [Fact]
    public async Task DailyLimit_AnUnreadableSetting_FallsBackToTheDefault_NotToUnlimited()
    {
        await _settings.SetAsync(SettingKeys.WishesPerDay, "kaputt", isProtected: false);

        for (var i = 0; i < UserLimits.DefaultWishesPerDay; i++)
            await WishSeriesAsync(_alice, FirstExtraSeries + i);

        Assert.False((await WishSeriesAsync(_alice, FirstExtraSeries + UserLimits.DefaultWishesPerDay)).Success);
    }

    [Fact]
    public async Task Search_IsThrottledPerUser_WithAHelpfulMessage()
    {
        await _settings.SetAsync(SettingKeys.TmdbSearchesPerMinute, "2", isProtected: false);
        var service = CreateService(FakeHttpHandler.Json(HttpStatusCode.OK, AniListSearchJson), new UserSearchThrottle(TimeProvider.System));

        Assert.Null((await service.SearchAsync(_alice.Id, "alles")).Error);
        Assert.Null((await service.SearchAsync(_alice.Id, "alles")).Error);
        var third = await service.SearchAsync(_alice.Id, "alles");
        var otherUser = await service.SearchAsync(_bob.Id, "alles");

        Assert.Contains("zu schnell", third.Error);
        Assert.Contains("Sekunden", third.Error);
        Assert.Empty(third.Results);
        Assert.Null(otherUser.Error);
    }

    [Fact]
    public async Task Search_WithALimitOfZero_IsNeverThrottled()
    {
        await _settings.SetAsync(SettingKeys.TmdbSearchesPerMinute, "0", isProtected: false);
        var service = CreateService(FakeHttpHandler.Json(HttpStatusCode.OK, AniListSearchJson), new UserSearchThrottle(TimeProvider.System));

        for (var i = 0; i < 30; i++)
            Assert.Null((await service.SearchAsync(_alice.Id, "alles")).Error);
    }

    [Fact]
    public async Task Search_RefusedBecauseOfTheLimit_DoesNotAskTmdb()
    {
        await _settings.SetAsync(SettingKeys.TmdbSearchesPerMinute, "1", isProtected: false);
        var service = CreateService(FakeHttpHandler.Json(HttpStatusCode.OK, AniListSearchJson), new UserSearchThrottle(TimeProvider.System));
        await service.SearchAsync(_alice.Id, "alles");
        var requestsBefore = _tmdbHandler.Paths.Count;

        await service.SearchAsync(_alice.Id, "alles");

        Assert.Equal(requestsBefore, _tmdbHandler.Paths.Count);
    }

    private async Task SeedAnonymityScenarioAsync()
    {
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, "für den Filmabend");
        await _service.CreateOrJoinAsync(_bob.Id, MatrixId, MediaKind.Movie, "ich auch, Bob hier");
        await _service.CreateOrJoinAsync(_bob.Id, ThronesId, MediaKind.Series, null);
    }

    [Fact]
    public async Task List_IsAnonymousByDefault_ForNormalUsers_AndShowsOnlyOwnContributionsAsYou()
    {
        await SeedAnonymityScenarioAsync();

        var wishes = await _service.ListAsync(_alice.Id, WishScope.All, null, WishSort.MostVotes);

        var matrix = wishes.Single(w => w.TmdbId == MatrixId);
        Assert.Equal(WishService.OwnName, matrix.CreatedByName);
        Assert.Equal([new WishNote(WishService.OwnName, "für den Filmabend"), new WishNote(null, "ich auch, Bob hier")], matrix.Notes);
        Assert.Null(wishes.Single(w => w.TmdbId == ThronesId).CreatedByName);
        Assert.Equal(2, matrix.Votes);
        Assert.True(matrix.VotedByMe);
    }

    [Fact]
    public async Task List_Anonymous_NeverContainsAnotherUsersNameAnywhere()
    {
        await SeedAnonymityScenarioAsync();

        var wishes = await _service.ListAsync(_alice.Id, WishScope.All, null, WishSort.Newest);

        var everything = string.Join("|", wishes.SelectMany(w => new[] { w.CreatedByName }.Concat(w.Notes.Select(n => n.UserName))).Where(s => s is not null));
        Assert.DoesNotContain("bob", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_Anonymous_StillWorksForMyWishes()
    {
        await SeedAnonymityScenarioAsync();

        var mine = await _service.ListAsync(_bob.Id, WishScope.Mine, null, WishSort.Newest);

        Assert.Equal(2, mine.Count);
        Assert.All(mine, w => Assert.True(w.VotedByMe));
    }

    [Fact]
    public async Task List_Anonymous_AdminsAlwaysSeeEveryName()
    {
        await SeedAnonymityScenarioAsync();

        var wishes = await _service.ListAsync(_admin.Id, WishScope.All, null, WishSort.MostVotes, viewerIsAdmin: true);

        var matrix = wishes.Single(w => w.TmdbId == MatrixId);
        Assert.Equal("alice", matrix.CreatedByName);
        Assert.Equal([new WishNote("alice", "für den Filmabend"), new WishNote("bob", "ich auch, Bob hier")], matrix.Notes);
        Assert.Equal("bob", wishes.Single(w => w.TmdbId == ThronesId).CreatedByName);
    }

    [Fact]
    public async Task List_WithAnonymityTurnedOff_ShowsNamesToEveryone_AndTheSwitchWorksImmediately()
    {
        await SeedAnonymityScenarioAsync();
        await _settings.SetAsync(SettingKeys.WishesAnonymous, "false", isProtected: false);

        var visible = await _service.ListAsync(_alice.Id, WishScope.All, null, WishSort.MostVotes);
        Assert.Equal("alice", visible.Single(w => w.TmdbId == MatrixId).CreatedByName);
        Assert.Equal("bob", visible.Single(w => w.TmdbId == ThronesId).CreatedByName);

        await _settings.SetAsync(SettingKeys.WishesAnonymous, "true", isProtected: false);

        var hidden = await _service.ListAsync(_alice.Id, WishScope.All, null, WishSort.MostVotes);
        Assert.Null(hidden.Single(w => w.TmdbId == ThronesId).CreatedByName);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("kaputt", true)]
    public void WishPrivacy_DefaultsToAnonymous_OnlyAnExplicitFalseShowsNames(string? stored, bool expected)
    {
        Assert.Equal(expected, WishPrivacy.IsAnonymous(stored));
    }

    [Fact]
    public async Task SearchAnime_AnnotatesAvailability_AndKeepsOnlyValidCoverPaths()
    {
        SeedAnimeMapping();
        _library.Update([JellyfinFixtures.LibraryItem("j1", "Schon da", MediaKind.Series, imdb: "tt0000888")], DateTime.UtcNow);
        await _service.CreateOrJoinAsync(_alice.Id, MatrixId, MediaKind.Movie, null);

        var outcome = await _service.SearchAnimeAsync(_alice.Id, "anohana");

        Assert.Null(outcome.Error);
        AnimeSearchResult Result(int id) => outcome.Results.Single(r => r.Anime.Id == id);
        Assert.Equal(WishAvailability.Wishable, Result(9989).Availability);
        Assert.Equal(WishAvailability.Wished, Result(15039).Availability);
        Assert.Equal(1, Result(15039).Votes);
        Assert.True(Result(15039).VotedByMe);
        Assert.DoesNotContain(outcome.Results, r => r.Anime.Id == 777);
        Assert.Equal(WishAvailability.InLibrary, Result(888).Availability);
        Assert.Equal("Anohana", Result(9989).Anime.DisplayTitle);
        Assert.Equal("/file/anilistcdn/media/anime/cover/medium/bx9989-hImMg6kCMm6I.jpg", Result(9989).Anime.CoverPath);
        Assert.Null(Result(15039).Anime.CoverPath);
    }

    [Fact]
    public async Task SearchAnime_HidesTitlesWithoutTmdbMapping_ButKeepsThemWhenAlreadyInTheLibrary()
    {
        SeedAnimeMapping();
        _library.Update([new LibraryItem("j1", "Ohne Zuordnung", MediaKind.Series, 2010, null, true, new Dictionary<string, string> { ["AniList"] = "777" })], DateTime.UtcNow);

        var outcome = await _service.SearchAnimeAsync(_alice.Id, "anohana");

        Assert.Equal(WishAvailability.InLibrary, outcome.Results.Single(r => r.Anime.Id == 777).Availability);
    }

    [Fact]
    public async Task SearchAnime_WithOnlyUnmappedHits_ReturnsAnEmptyListWithoutError()
    {
        var outcome = await _service.SearchAnimeAsync(_alice.Id, "anohana");

        Assert.Null(outcome.Error);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task SearchAnime_RecognisesTitlesViaAniListPluginId()
    {
        SeedAnimeMapping();
        _library.Update([new LibraryItem("j1", "Anohana", MediaKind.Series, 2011, null, true, new Dictionary<string, string> { ["AniList"] = "9989" })], DateTime.UtcNow);

        var outcome = await _service.SearchAnimeAsync(_alice.Id, "anohana");

        Assert.Equal(WishAvailability.InLibrary, outcome.Results.Single(r => r.Anime.Id == 9989).Availability);
    }

    [Fact]
    public async Task SearchAnime_ReturnsErrorsInsteadOfThrowing()
    {
        Assert.Empty((await _service.SearchAnimeAsync(_alice.Id, "  ")).Results);
        Assert.NotNull((await _service.SearchAnimeAsync(_alice.Id, new string('x', WishValidation.MaxQueryLength + 1))).Error);

        var limited = CreateService(FakeHttpHandler.Json(HttpStatusCode.TooManyRequests));
        Assert.Contains("keine Anfragen", (await limited.SearchAnimeAsync(_alice.Id, "anohana")).Error);
    }

    [Fact]
    public async Task CreateOrJoinAnime_CreatesTheSameWishAsTheTmdbPath()
    {
        SeedAnimeMapping();

        var first = await _service.CreateOrJoinAnimeAsync(_alice.Id, 9989, "Anime-Abend");
        var second = await _service.CreateOrJoinAsync(_bob.Id, ThronesId, MediaKind.Series, null);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        using var db = _db.CreateDbContext();
        var wish = db.Wishes.Single();
        Assert.Equal(ThronesId, wish.TmdbId);
        Assert.Equal(MediaKind.Series, wish.Kind);
        Assert.Equal(2, db.WishVotes.Count());
    }

    [Theory]
    [InlineData(777)]
    [InlineData(888)]
    [InlineData(123456)]
    public async Task CreateOrJoinAnime_WithoutTmdbMapping_IsRejected(int aniListId)
    {
        SeedAnimeMapping();

        var result = await _service.CreateOrJoinAnimeAsync(_alice.Id, aniListId, null);

        Assert.False(result.Success);
        Assert.Contains("Zuordnung", result.Message);
        using var db = _db.CreateDbContext();
        Assert.Empty(db.Wishes);
    }

    [Fact]
    public async Task CreateOrJoinAnime_TitleAlreadyInJellyfin_IsRejected()
    {
        SeedAnimeMapping();
        _library.Update([JellyfinFixtures.LibraryItem("j1", "Matrix", imdb: "tt0133093")], DateTime.UtcNow);

        var result = await _service.CreateOrJoinAnimeAsync(_alice.Id, 15039, null);

        Assert.False(result.Success);
        Assert.Contains("Jellyfin", result.Message);
    }

    private int WishId(int tmdbId = MatrixId)
    {
        using var db = _db.CreateDbContext();
        return db.Wishes.Single(w => w.TmdbId == tmdbId).Id;
    }

    /// <summary>Stellt TMDB nach: Titel 603 (Film), 1399 (Serie) und beliebig nachrüstbare Serien. Thread-sicher für parallele Wünsche.</summary>
    private sealed class StubTmdbHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _paths = [];
        private readonly Dictionary<string, string> _details = new()
        {
            ["/3/movie/603"] = """{"id":603,"title":"Matrix","release_date":"1999-03-30","poster_path":"/matrix1.jpg","imdb_id":"tt0133093"}""",
            ["/3/tv/1399"] = """{"id":1399,"name":"Game of Thrones","first_air_date":"2011-04-17","poster_path":"/thrones1.jpg","external_ids":{"imdb_id":"tt0944947"}}"""
        };
        private readonly List<string> _extraSearchResults = [];

        public bool Fail { get; set; }

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_gate) return _paths.ToList();
            }
        }

        public IEnumerable<string> DetailRequests(int tmdbId)
        {
            lock (_gate) return _paths.Where(p => p.EndsWith($"/{tmdbId}")).ToList();
        }

        public void SetSeries(int tmdbId, string name)
        {
            lock (_gate)
            {
                _details[$"/3/tv/{tmdbId}"] = $$"""{"id":{{tmdbId}},"name":"{{name}}","first_air_date":"2020-01-01"}""";
                _extraSearchResults.Add($$"""{"id":{{tmdbId}},"media_type":"tv","name":"{{name}}","first_air_date":"2020-01-01"}""");
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_gate)
            {
                _paths.Add(path);
                if (Fail)
                    throw new HttpRequestException("TMDB down");

                string? json = null;
                if (_details.TryGetValue(path, out var detail))
                    json = detail;
                else if (path == "/3/search/multi")
                {
                    var results = new List<string>
                    {
                        """{"id":603,"media_type":"movie","title":"Matrix","release_date":"1999-03-30","poster_path":"/matrix1.jpg"}""",
                        """{"id":1399,"media_type":"tv","name":"Game of Thrones","first_air_date":"2011-04-17"}"""
                    };
                    results.AddRange(_extraSearchResults);
                    json = $$"""{"results":[{{string.Join(',', results)}}]}""";
                }
                else if (path == "/3/find/tt0133093")
                    json = """{"movie_results":[{"id":603,"title":"Matrix","release_date":"1999-03-30"}],"tv_results":[]}""";

                return Task.FromResult(json is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
        }
    }
}
