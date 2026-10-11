using Microsoft.EntityFrameworkCore;
using Wunschkino.Anime;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Resources;
using Wunschkino.Services;

namespace Wunschkino.Wishes;

public enum WishAvailability
{
    Wishable,
    InLibrary,
    Wished
}

public enum WishScope
{
    Mine,
    All
}

public enum WishSort
{
    Newest,
    MostVotes
}

public sealed record WishSearchResult(TmdbTitle Title, WishAvailability Availability, WishStatus? Status, int Votes, bool VotedByMe);

public sealed record WishSearchOutcome(IReadOnlyList<WishSearchResult> Results, string? Error);

public sealed record AnimeSearchResult(AniListAnime Anime, WishAvailability Availability, WishStatus? Status, int Votes, bool VotedByMe);

public sealed record AnimeSearchOutcome(IReadOnlyList<AnimeSearchResult> Results, string? Error);

/// <param name="UserName">Null, wenn der Name für den Betrachter verborgen ist (anonyme Wünsche).</param>
public sealed record WishNote(string? UserName, string Text);

public sealed record WishListItem(
    int Id,
    int TmdbId,
    MediaKind Kind,
    string Title,
    int? Year,
    string? PosterPath,
    WishStatus Status,
    string? AdminComment,
    int Votes,
    string? CreatedByName,
    DateTime CreatedUtc,
    bool VotedByMe,
    IReadOnlyList<WishNote> Notes,
    bool CompletedAutomatically);

public sealed record WishResult(bool Success, string Message)
{
    public static WishResult Ok(string message) => new(true, message);
    public static WishResult Fail(string message) => new(false, message);
}

/// <summary>Scoped, weil der typisierte <see cref="TmdbClient"/> es auch ist (HttpClient-Handler rotieren).</summary>
public sealed class WishService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    TmdbClient tmdb,
    AniListClient aniList,
    AnimeIdMapping animeMapping,
    LibraryCache library,
    TimeProvider time,
    UserSearchThrottle? searchThrottle = null)
{
    private const int MaxSearchResults = 20;
    private const int MaxListedWishes = 500;

    /// <summary>So erscheinen die eigenen Beiträge, wenn die Namen anderer verborgen sind.</summary>
    public const string OwnName = "du";

    public async Task<WishSearchOutcome> SearchAsync(int userId, string? query, CancellationToken ct = default)
    {
        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new WishSearchOutcome([], null);
        if (trimmed.Length > WishValidation.MaxQueryLength)
            return Failed(string.Format(Texts.Culture, Texts.Msg_Wish_SearchTooLong, WishValidation.MaxQueryLength));

        var apiKey = await settings.GetAsync(SettingKeys.TmdbApiKey, ct);
        if (apiKey is null)
            return Failed(Texts.Msg_Wish_TmdbNotConfiguredAdmin);
        if (await SearchLimitMessageAsync(userId, ct) is { } tooFast)
            return Failed(tooFast);

        IReadOnlyList<TmdbTitle> titles;
        try
        {
            titles = WishValidation.IsImdbId(trimmed)
                ? await tmdb.FindByImdbAsync(apiKey, trimmed.ToLowerInvariant(), ct)
                : await tmdb.SearchAsync(apiKey, trimmed, ct);
        }
        catch (TmdbException ex)
        {
            return Failed(ex.Message);
        }
        titles = titles.Take(MaxSearchResults).ToList();

        var tmdbIds = titles.Select(t => t.TmdbId).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wishes = (await db.Wishes.AsNoTracking()
                .Where(w => tmdbIds.Contains(w.TmdbId))
                .Select(w => new { w.TmdbId, w.Kind, w.Status, Votes = w.Votes.Count, Mine = w.Votes.Any(v => v.UserId == userId) })
                .ToListAsync(ct))
            .ToDictionary(w => (w.TmdbId, w.Kind));

        var results = titles.Select(title =>
        {
            if (IsInLibrary(title))
                return new WishSearchResult(title, WishAvailability.InLibrary, null, 0, false);
            return wishes.TryGetValue((title.TmdbId, title.Kind), out var wish)
                ? new WishSearchResult(title, WishAvailability.Wished, wish.Status, wish.Votes, wish.Mine)
                : new WishSearchResult(title, WishAvailability.Wishable, null, 0, false);
        }).ToList();
        return new WishSearchOutcome(results, null);
    }

    public async Task<AnimeSearchOutcome> SearchAnimeAsync(int userId, string? query, CancellationToken ct = default)
    {
        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length == 0)
            return new AnimeSearchOutcome([], null);
        if (trimmed.Length > WishValidation.MaxQueryLength)
            return AnimeFailed(string.Format(Texts.Culture, Texts.Msg_Wish_SearchTooLong, WishValidation.MaxQueryLength));

        IReadOnlyList<AniListAnime> animes;
        try
        {
            animes = await aniList.SearchAsync(trimmed, userId, ct);
        }
        catch (AniListException ex)
        {
            return AnimeFailed(ex.Message);
        }

        var mappings = animes.ToDictionary(a => a.Id, a => animeMapping.Find(a.Id));
        var tmdbIds = mappings.Values.Where(m => m is { HasTmdb: true }).Select(m => m!.TmdbId!.Value).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var wishes = (await db.Wishes.AsNoTracking()
                .Where(w => tmdbIds.Contains(w.TmdbId))
                .Select(w => new { w.TmdbId, w.Kind, w.Status, Votes = w.Votes.Count, Mine = w.Votes.Any(v => v.UserId == userId) })
                .ToListAsync(ct))
            .ToDictionary(w => (w.TmdbId, w.Kind));

        var results = animes.Select(anime =>
        {
            var mapping = mappings[anime.Id];
            if (IsInLibrary(anime.Id, mapping))
                return new AnimeSearchResult(anime, WishAvailability.InLibrary, null, 0, false);
            // Ohne TMDB-ID lässt sich kein Wunsch anlegen: solche Treffer zeigen wir gar nicht erst an.
            if (mapping is not { HasTmdb: true })
                return null;
            return wishes.TryGetValue((mapping.TmdbId!.Value, mapping.Kind!.Value), out var wish)
                ? new AnimeSearchResult(anime, WishAvailability.Wished, wish.Status, wish.Votes, wish.Mine)
                : new AnimeSearchResult(anime, WishAvailability.Wishable, null, 0, false);
        }).OfType<AnimeSearchResult>().ToList();
        return new AnimeSearchOutcome(results, null);
    }

    /// <summary>Wünscht einen Anime über seine TMDB-Zuordnung – es entsteht derselbe Wunsch wie über die normale Suche.</summary>
    public Task<WishResult> CreateOrJoinAnimeAsync(int userId, int aniListId, string? note, CancellationToken ct = default)
    {
        var mapping = animeMapping.Find(aniListId);
        if (mapping is not { HasTmdb: true })
            return Task.FromResult(WishResult.Fail(Texts.Msg_Wish_AnimeNoTmdb));
        if (IsInLibrary(aniListId, mapping))
            return Task.FromResult(WishResult.Fail(Texts.Msg_Wish_AlreadyInLibrary));

        return CreateOrJoinAsync(userId, mapping.TmdbId!.Value, mapping.Kind!.Value, note, ct);
    }

    /// <summary>Legt den Wunsch an oder schließt sich einem bestehenden per „+1“ an. Metadaten holt der Server selbst von TMDB.</summary>
    public async Task<WishResult> CreateOrJoinAsync(int userId, int tmdbId, MediaKind kind, string? note, CancellationToken ct = default)
    {
        var cleanNote = WishValidation.NormalizeText(note, WishValidation.MaxNoteLength, out var noteTooLong);
        if (noteTooLong)
            return WishResult.Fail(string.Format(Texts.Culture, Texts.Msg_Wish_NoteTooLong, WishValidation.MaxNoteLength));
        if (tmdbId <= 0 || !Enum.IsDefined(kind))
            return WishResult.Fail(Texts.Msg_Wish_InvalidTitle);
        if (library.FindByTmdb(kind, tmdbId.ToString()) is not null)
            return WishResult.Fail(Texts.Msg_Wish_AlreadyInLibrary);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await IsActiveUserAsync(db, userId, ct))
            return WishResult.Fail(Texts.Msg_Wish_AccountInactive);
        if (await DailyLimitMessageAsync(db, userId, ct) is { } limitReached)
            return WishResult.Fail(limitReached);
        await CleanUpActionsAsync(db, ct);

        var existing = await db.Wishes.Include(w => w.Votes).SingleOrDefaultAsync(w => w.TmdbId == tmdbId && w.Kind == kind, ct);
        if (existing is not null)
            return await JoinAsync(db, existing, userId, cleanNote, ct);

        var apiKey = await settings.GetAsync(SettingKeys.TmdbApiKey, ct);
        if (apiKey is null)
            return WishResult.Fail(Texts.Msg_Wish_TmdbNotConfigured);
        // Jeder neue Wunsch fragt TMDB nach den Einzelheiten, auch mit erfundenen Nummern: Das zählt, wie eine Suche, pro Minute.
        if (await SearchLimitMessageAsync(-userId, ct, Texts.Msg_Wish_CreatingTooFast) is { } tooFast)
            return WishResult.Fail(tooFast);

        TmdbTitle? details;
        try
        {
            details = await tmdb.GetDetailsAsync(apiKey, tmdbId, kind, ct);
        }
        catch (TmdbException ex)
        {
            return WishResult.Fail(ex.Message);
        }
        if (details is null)
            return WishResult.Fail(Texts.Msg_Wish_NotOnTmdb);
        if (details.ImdbId is { } imdbId && library.FindByImdb(imdbId) is not null)
            return WishResult.Fail(Texts.Msg_Wish_AlreadyInLibrary);

        var now = time.GetUtcNow().UtcDateTime;
        db.Wishes.Add(new Wish
        {
            TmdbId = details.TmdbId,
            Kind = details.Kind,
            Title = details.Title,
            Year = details.Year,
            PosterPath = details.PosterPath,
            ImdbId = details.ImdbId,
            MetadataFetchedUtc = now,
            Status = WishStatus.Open,
            CreatedByUserId = userId,
            CreatedUtc = now,
            UpdatedUtc = now,
            Votes = [new WishVote { UserId = userId, Note = cleanNote, CreatedUtc = now }]
        });
        db.WishActions.Add(new WishAction { UserId = userId, TimeUtc = now });
        try
        {
            await db.SaveChangesAsync(ct);
            return WishResult.Ok(string.Format(Texts.Culture, Texts.Msg_Wish_Created, details.Title));
        }
        catch (DbUpdateException)
        {
            // Jemand war gleichzeitig schneller: der Unique-Index auf (TmdbId, Kind) hat den zweiten Wunsch abgewiesen.
            await using var retryDb = await dbFactory.CreateDbContextAsync(ct);
            var winner = await retryDb.Wishes.Include(w => w.Votes).SingleOrDefaultAsync(w => w.TmdbId == tmdbId && w.Kind == kind, ct);
            if (winner is null)
                throw;
            return await JoinAsync(retryDb, winner, userId, cleanNote, ct);
        }
    }

    /// <summary>
    /// Zieht die eigene Stimme zurück. War es die einzige Stimme, verschwindet der Wunsch – aber nur, solange er noch offen ist.
    /// </summary>
    public async Task<WishResult> WithdrawAsync(int userId, int wishId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await IsActiveUserAsync(db, userId, ct))
            return WishResult.Fail(Texts.Msg_Wish_AccountInactive);

        var wish = await db.Wishes.AsNoTracking().Include(w => w.Votes).SingleOrDefaultAsync(w => w.Id == wishId, ct);
        if (wish is null)
            return WishResult.Fail(Texts.Msg_Wish_Gone);
        if (wish.Votes.All(v => v.UserId != userId))
            return WishResult.Fail(Texts.Msg_Wish_NotSupporting);
        if (wish.Status is WishStatus.Rejected or WishStatus.Done)
            return WishResult.Fail(Texts.Msg_Wish_CannotWithdraw);
        if (wish.Votes.Count == 1 && wish.Status != WishStatus.Open)
            return WishResult.Fail(Texts.Msg_Wish_AcceptedOnlyAdmin);

        // Bedingte Löschungen statt „laden, entscheiden, speichern“: Gleichzeitiges Zurückziehen oder Beitreten
        // kann so weder eine Stimme mitreißen (Cascade) noch einen Wunsch ohne Stimmen zurücklassen.
        await db.WishVotes.Where(v => v.WishId == wishId && v.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Wishes.Where(w => w.Id == wishId && w.Status == WishStatus.Open && !w.Votes.Any()).ExecuteDeleteAsync(ct);
        return WishResult.Ok(Texts.Msg_Wish_Withdrawn);
    }

    public async Task<WishResult> DeleteAsync(int adminId, int wishId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AdminCheck.IsActiveAdminAsync(db, adminId, ct))
            return WishResult.Fail(Texts.Msg_Wish_AdminRequired);

        var wish = await db.Wishes.SingleOrDefaultAsync(w => w.Id == wishId, ct);
        if (wish is null)
            return WishResult.Fail(Texts.Msg_Wish_Gone);
        db.Wishes.Remove(wish);
        await db.SaveChangesAsync(ct);
        return WishResult.Ok(Texts.Msg_Wish_Deleted);
    }

    public async Task<WishResult> SetStatusAsync(int adminId, int wishId, WishStatus status, string? comment, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(status))
            return WishResult.Fail(Texts.Msg_Wish_InvalidStatus);
        var cleanComment = WishValidation.NormalizeText(comment, WishValidation.MaxCommentLength, out var tooLong);
        if (tooLong)
            return WishResult.Fail(string.Format(Texts.Culture, Texts.Msg_Wish_CommentTooLong, WishValidation.MaxCommentLength));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AdminCheck.IsActiveAdminAsync(db, adminId, ct))
            return WishResult.Fail(Texts.Msg_Wish_AdminRequired);

        var wish = await db.Wishes.SingleOrDefaultAsync(w => w.Id == wishId, ct);
        if (wish is null)
            return WishResult.Fail(Texts.Msg_Wish_Gone);
        if (status != wish.Status)
        {
            // Wer einen erledigten Wunsch wieder öffnet, übernimmt die Kontrolle: Der Abgleich würde ihn sonst beim
            // nächsten Refresh erneut schließen, solange der Titel in Jellyfin liegt. Jeder andere Wechsel löscht nur
            // die Kennzeichnung „automatisch erledigt“. Ein reiner Kommentar (gleicher Status) ändert nichts daran.
            wish.AutoCompletion = NextAutoCompletion(wish.Status, wish.AutoCompletion, status);
            // Der Abgleich schreibt per ExecuteUpdate an EF vorbei. Ohne diese Markierung ließe EF die Spalte aus, wenn
            // sie dem beim Laden gelesenen Wert gleicht, und eine Admin-Entscheidung trüge das Etikett „automatisch“.
            db.Entry(wish).Property(w => w.AutoCompletion).IsModified = true;
        }
        wish.Status = status;
        wish.AdminComment = cleanComment;
        wish.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return WishResult.Ok(Texts.Msg_Wish_StatusSaved);
    }

    /// <param name="viewerIsAdmin">Admins sehen immer, von wem ein Wunsch kommt. Alle anderen nur, wenn die Wünsche nicht anonym sind;
    /// die eigenen Beiträge erscheinen dann als „du“. Die Namen verlassen den Server gar nicht erst, wenn sie verborgen sind.</param>
    public async Task<IReadOnlyList<WishListItem>> ListAsync(
        int userId, WishScope scope, WishStatus? status, WishSort sort, bool viewerIsAdmin = false, CancellationToken ct = default)
    {
        var hideNames = !viewerIsAdmin && WishPrivacy.IsAnonymous(await settings.GetAsync(SettingKeys.WishesAnonymous, ct));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<Wish> query = db.Wishes.AsNoTracking();
        if (scope == WishScope.Mine)
            query = query.Where(w => w.Votes.Any(v => v.UserId == userId));
        if (status is { } filter)
            query = query.Where(w => w.Status == filter);

        var wishes = await query.Include(w => w.Votes).ThenInclude(v => v.User).ToListAsync(ct);
        var ordered = sort == WishSort.MostVotes
            ? wishes.OrderByDescending(w => w.Votes.Count).ThenByDescending(w => w.CreatedUtc)
            : wishes.OrderByDescending(w => w.CreatedUtc);
        var page = ordered.Take(MaxListedWishes).ToList();

        var creatorIds = page.Select(w => w.CreatedByUserId).Distinct().ToList();
        var creatorNames = await db.Users.AsNoTracking()
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        return page.Select(w => new WishListItem(
            w.Id,
            w.TmdbId,
            w.Kind,
            w.Title,
            w.Year,
            w.PosterPath,
            w.Status,
            w.AdminComment,
            w.Votes.Count,
            hideNames
                ? (w.CreatedByUserId == userId ? OwnName : null)
                : creatorNames.GetValueOrDefault(w.CreatedByUserId, "?"),
            w.CreatedUtc,
            w.Votes.Any(v => v.UserId == userId),
            w.Votes.Where(v => !string.IsNullOrEmpty(v.Note))
                .OrderBy(v => v.CreatedUtc)
                .Select(v => new WishNote(
                    hideNames ? (v.UserId == userId ? OwnName : null) : v.User?.Username ?? "?",
                    v.Note!))
                .ToList(),
            w.AutoCompletion == WishAutoCompletion.Completed)).ToList();
    }

    private async Task<WishResult> JoinAsync(AppDbContext db, Wish wish, int userId, string? note, CancellationToken ct)
    {
        if (wish.Status == WishStatus.Rejected)
            return WishResult.Fail(Texts.Msg_Wish_Rejected);
        if (wish.Status == WishStatus.Done)
            return WishResult.Fail(Texts.Msg_Wish_AlreadyDone);
        if (wish.Votes.Any(v => v.UserId == userId))
            return WishResult.Fail(Texts.Msg_Wish_AlreadySupported);

        var now = time.GetUtcNow().UtcDateTime;
        db.WishVotes.Add(new WishVote { WishId = wish.Id, UserId = userId, Note = note, CreatedUtc = now });
        db.WishActions.Add(new WishAction { UserId = userId, TimeUtc = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Meist der Primärschlüssel (WishId, UserId) bei einem Doppelklick, möglich ist auch ein zwischenzeitlich gelöschter Wunsch.
            return WishResult.Fail(Texts.Msg_Wish_VoteFailed);
        }
        return WishResult.Ok(string.Format(Texts.Culture, Texts.Msg_Wish_Supported, wish.Title));
    }

    /// <summary>
    /// Zählt neue Wünsche und „+1“ der letzten 24 Stunden aus <c>WishActions</c> (nicht aus den Stimmen: Zurückziehen löscht sie).
    /// Admins sind ausgenommen, 0 heißt kein Limit.
    /// </summary>
    private async Task<string?> DailyLimitMessageAsync(AppDbContext db, int userId, CancellationToken ct)
    {
        var limit = UserLimits.ParseWishesPerDay(await settings.GetAsync(SettingKeys.WishesPerDay, ct));
        if (limit == 0)
            return null;
        if (await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin, ct))
            return null;

        var since = time.GetUtcNow().UtcDateTime.AddHours(-24);
        var used = await db.WishActions.CountAsync(a => a.UserId == userId && a.TimeUtc > since, ct);
        return used >= limit
            ? string.Format(Texts.Culture, Texts.Msg_Wish_DailyLimit, limit)
            : null;
    }

    /// <param name="throttleKey">Die Nutzer-ID für Suchen; die negative ID zählt Neuanlagen getrennt davon.</param>
    private async Task<string?> SearchLimitMessageAsync(int throttleKey, CancellationToken ct, string? tooFastText = null)
    {
        if (searchThrottle is null)
            return null;

        var limit = UserLimits.ParseSearchesPerMinute(await settings.GetAsync(SettingKeys.TmdbSearchesPerMinute, ct));
        return searchThrottle.TryAcquire(throttleKey, limit) is { } wait
            ? string.Format(Texts.Culture, Texts.Msg_Wish_TooFastWait, tooFastText ?? Texts.Msg_Wish_SearchingTooFast, Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)))
            : null;
    }

    private static long _lastActionCleanupTicks;

    /// <summary>Löscht gezählte Handlungen, die für das 24-Stunden-Fenster nicht mehr gebraucht werden; höchstens einmal pro Stunde.</summary>
    private async Task CleanUpActionsAsync(AppDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var last = Interlocked.Read(ref _lastActionCleanupTicks);
        if (last != 0 && now.Ticks - last < TimeSpan.FromHours(1).Ticks)
            return;
        if (Interlocked.CompareExchange(ref _lastActionCleanupTicks, now.Ticks, last) != last)
            return;

        try
        {
            var oldest = now.AddDays(-2);
            await db.WishActions.Where(a => a.TimeUtc < oldest).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Das Aufräumen darf keinen Wunsch verhindern; der nächste Lauf versucht es wieder.
        }
    }

    private bool IsInLibrary(TmdbTitle title) =>
        library.FindByTmdb(title.Kind, title.TmdbId.ToString()) is not null
        || (title.ImdbId is { } imdbId && library.FindByImdb(imdbId) is not null);

    /// <summary>Wie sich die Beziehung zum automatischen Abgleich bei einem Statuswechsel durch einen Admin ändert.</summary>
    public static WishAutoCompletion NextAutoCompletion(WishStatus current, WishAutoCompletion completion, WishStatus next)
    {
        if (next == current)
            return completion;
        if (completion == WishAutoCompletion.Suppressed)
            return WishAutoCompletion.Suppressed;
        var reopened = current == WishStatus.Done && (next == WishStatus.Open || next == WishStatus.Accepted);
        return reopened ? WishAutoCompletion.Suppressed : WishAutoCompletion.None;
    }

    private bool IsInLibrary(int aniListId, AnimeMapping? mapping) =>
        library.FindByAniList(aniListId.ToString()) is not null
        || (mapping is { HasTmdb: true } && library.FindByTmdb(mapping.Kind!.Value, mapping.TmdbId!.Value.ToString()) is not null)
        || (mapping?.ImdbIds.Any(imdbId => library.FindByImdb(imdbId) is not null) ?? false);

    private static AnimeSearchOutcome AnimeFailed(string error) => new([], error);

    private static Task<bool> IsActiveUserAsync(AppDbContext db, int userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && !u.IsDisabled, ct);

    private static WishSearchOutcome Failed(string error) => new([], error);
}
