using Microsoft.EntityFrameworkCore;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Resources;
using Wunschkino.Services;

namespace Wunschkino.Setup;

public sealed record SetupRequest(
    string Token,
    string AdminUsername,
    string AdminPassword,
    string JellyfinUrl,
    string JellyfinApiKey,
    string TmdbApiKey,
    bool AnonymousWishes = true);

public sealed record SetupResult(bool Success, string? Error)
{
    public static SetupResult Ok() => new(true, null);
    public static SetupResult Fail(string error) => new(false, error);
}

public sealed class SetupService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    IPasswordHasher passwordHasher,
    SetupTokenStore tokenStore,
    SetupState state,
    TimeProvider time,
    AuditLog? audit = null)
{
    private readonly SemaphoreSlim _completionLock = new(1, 1);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(ct))
        {
            state.MarkCompleted();
            tokenStore.Delete();
            return;
        }
        tokenStore.EnsureExists();
    }

    public bool IsTokenValid(string? token) => !state.IsCompleted && tokenStore.IsValid(token);

    public async Task<SetupResult> CompleteAsync(SetupRequest request, CancellationToken ct = default)
    {
        if (ValidateInput(request) is { } inputError)
            return SetupResult.Fail(inputError);

        await _completionLock.WaitAsync(ct);
        try
        {
            if (state.IsCompleted || !tokenStore.IsValid(request.Token))
                return SetupResult.Fail(Texts.Msg_Setup_TokenInvalid);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Users.AnyAsync(ct))
                return SetupResult.Fail(Texts.Msg_Setup_AlreadyDone);

            var admin = new User
            {
                Username = request.AdminUsername.Trim(),
                PasswordHash = passwordHasher.Hash(request.AdminPassword),
                Role = UserRole.Admin,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime
            };
            db.Users.Add(admin);
            await settings.StageAsync(db, SettingKeys.JellyfinUrl, ConnectionValidation.NormalizeUrl(request.JellyfinUrl), isProtected: false, ct);
            await settings.StageAsync(db, SettingKeys.JellyfinApiKey, request.JellyfinApiKey.Trim(), isProtected: true, ct);
            await settings.StageAsync(db, SettingKeys.TmdbApiKey, request.TmdbApiKey.Trim(), isProtected: true, ct);
            await settings.StageAsync(db, SettingKeys.WishesAnonymous, request.AnonymousWishes ? "true" : "false", isProtected: false, ct);
            await db.SaveChangesAsync(ct);

            tokenStore.Delete();
            state.MarkCompleted();
            if (audit is not null)
                await audit.WriteAsync(AuditActions.SetupCompleted, admin.Id, admin.Username, ct: ct);
            return SetupResult.Ok();
        }
        finally
        {
            _completionLock.Release();
        }
    }

    private static string? ValidateInput(SetupRequest request) =>
        UserValidation.ValidateUsername(request.AdminUsername)
        ?? UserValidation.ValidatePassword(request.AdminPassword)
        ?? ConnectionValidation.ValidateJellyfinUrl(request.JellyfinUrl)
        ?? ConnectionValidation.ValidateApiKey(request.JellyfinApiKey, "Jellyfin")
        ?? ConnectionValidation.ValidateApiKey(request.TmdbApiKey, "TMDB");
}
