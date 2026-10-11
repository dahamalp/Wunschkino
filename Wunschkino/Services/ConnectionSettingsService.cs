using Microsoft.EntityFrameworkCore;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Resources;

namespace Wunschkino.Services;

/// <summary>Was die Einstellungsseite anzeigen darf – die API-Keys selbst verlassen nie den Server.</summary>
public sealed record ConnectionSettingsView(string? JellyfinUrl, bool HasJellyfinApiKey, bool HasTmdbApiKey, int RefreshMinutes);

/// <summary>Leere Keys bedeuten „gespeicherten Key behalten“.</summary>
/// <param name="CurrentPassword">Passwort des Admins; nötig, sobald sich die Adresse von Jellyfin ändert oder ein neuer Key kommt.</param>
public sealed record ConnectionSettingsUpdate(
    string JellyfinUrl, string? NewJellyfinApiKey, string? NewTmdbApiKey, int RefreshMinutes, string? CurrentPassword = null);

public sealed record ConnectionSaveResult(bool Success, IReadOnlyList<string> Messages);

/// <summary>Scoped, weil die typisierten HttpClients nicht in einem Singleton festgehalten werden sollen.</summary>
public sealed class ConnectionSettingsService(
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    JellyfinClient jellyfin,
    TmdbClient tmdb,
    UserService users,
    AuditLog? audit = null)
{
    public const int DefaultRefreshMinutes = 15;
    public const int MinRefreshMinutes = 5;
    public const int MaxRefreshMinutes = 1440;

    public async Task<ConnectionSettingsView> GetAsync(CancellationToken ct = default) =>
        new(
            await settings.GetAsync(SettingKeys.JellyfinUrl, ct),
            await settings.GetAsync(SettingKeys.JellyfinApiKey, ct) is not null,
            await settings.GetAsync(SettingKeys.TmdbApiKey, ct) is not null,
            await GetRefreshMinutesAsync(ct));

    public async Task<int> GetRefreshMinutesAsync(CancellationToken ct = default) =>
        int.TryParse(await settings.GetAsync(SettingKeys.JellyfinRefreshMinutes, ct), out var minutes)
            ? Math.Clamp(minutes, MinRefreshMinutes, MaxRefreshMinutes)
            : DefaultRefreshMinutes;

    /// <summary>Speichert nur, wenn beide Verbindungen mit den künftig wirksamen Werten funktionieren.</summary>
    public async Task<ConnectionSaveResult> SaveAsync(int actingUserId, ConnectionSettingsUpdate update, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await AdminCheck.IsActiveAdminAsync(db, actingUserId, ct))
            return Fail(Texts.Msg_User_NotAllowed);

        var newJellyfinKey = Blank(update.NewJellyfinApiKey);
        var newTmdbKey = Blank(update.NewTmdbApiKey);
        if (ConnectionValidation.ValidateJellyfinUrl(update.JellyfinUrl) is { } urlError)
            return Fail(urlError);
        if (update.RefreshMinutes is < MinRefreshMinutes or > MaxRefreshMinutes)
            return Fail(string.Format(Texts.Culture, Texts.Msg_Connection_Interval, MinRefreshMinutes, MaxRefreshMinutes));
        if (newJellyfinKey is not null && ConnectionValidation.ValidateApiKey(newJellyfinKey, "Jellyfin") is { } jellyfinKeyError)
            return Fail(jellyfinKeyError);
        if (newTmdbKey is not null && ConnectionValidation.ValidateApiKey(newTmdbKey, "TMDB") is { } tmdbKeyError)
            return Fail(tmdbKeyError);

        var url = ConnectionValidation.NormalizeUrl(update.JellyfinUrl);
        var storedUrl = await settings.GetAsync(SettingKeys.JellyfinUrl, ct);
        // Der gespeicherte Key darf nie an eine andere Adresse gehen, sonst ließe er sich mit einem gestohlenen Admin-Cookie zu einem fremden Server schicken.
        var endpointChanged = ChangesEndpoint(storedUrl, url);
        if (endpointChanged && newJellyfinKey is null)
            return Fail(Texts.Msg_Connection_KeyNeededForNewAddress);
        if (endpointChanged || newJellyfinKey is not null || newTmdbKey is not null)
        {
            var confirmed = await users.ConfirmPasswordAsync(actingUserId, update.CurrentPassword, ct);
            if (!confirmed.Success)
                return Fail(confirmed.Error ?? Texts.Msg_User_CurrentPasswordWrong);
        }

        var jellyfinKey = newJellyfinKey ?? await settings.GetAsync(SettingKeys.JellyfinApiKey, ct);
        var tmdbKey = newTmdbKey ?? await settings.GetAsync(SettingKeys.TmdbApiKey, ct);
        if (jellyfinKey is null || tmdbKey is null)
            return Fail(Texts.Msg_Connection_KeyUnreadable);

        var jellyfinTest = await jellyfin.TestConnectionAsync(url, jellyfinKey, ct);
        var tmdbTest = await tmdb.TestConnectionAsync(tmdbKey, ct);
        var messages = new[] { jellyfinTest.Message, tmdbTest.Message };
        if (!jellyfinTest.Success || !tmdbTest.Success)
            return new ConnectionSaveResult(false, messages);

        var changes = new List<string>();
        if (!string.Equals(storedUrl, url, StringComparison.Ordinal))
            changes.Add("Jellyfin-Adresse");
        if (newJellyfinKey is not null)
            changes.Add("Jellyfin-Key");
        if (newTmdbKey is not null)
            changes.Add("TMDB-Key");
        if (await GetRefreshMinutesAsync(ct) != update.RefreshMinutes)
            changes.Add($"Intervall {update.RefreshMinutes} Minuten");

        await settings.StageAsync(db, SettingKeys.JellyfinUrl, url, isProtected: false, ct);
        await settings.StageAsync(db, SettingKeys.JellyfinRefreshMinutes, update.RefreshMinutes.ToString(), isProtected: false, ct);
        if (newJellyfinKey is not null)
            await settings.StageAsync(db, SettingKeys.JellyfinApiKey, newJellyfinKey, isProtected: true, ct);
        if (newTmdbKey is not null)
            await settings.StageAsync(db, SettingKeys.TmdbApiKey, newTmdbKey, isProtected: true, ct);
        await db.SaveChangesAsync(ct);

        if (audit is not null && changes.Count > 0)
        {
            var actorName = await db.Users.AsNoTracking().Where(u => u.Id == actingUserId).Select(u => u.Username).SingleOrDefaultAsync(ct);
            await audit.WriteAsync(AuditActions.ConnectionsChanged, actingUserId, actorName, detail: string.Join(", ", changes), ct: ct);
        }
        return new ConnectionSaveResult(true, messages);
    }

    /// <summary>Wahr, wenn Schema, Host oder Port anders sind. Ein anderer Pfad bleibt derselbe Server.</summary>
    public static bool ChangesEndpoint(string? storedUrl, string newUrl)
    {
        if (!Uri.TryCreate(storedUrl, UriKind.Absolute, out var stored) || !Uri.TryCreate(newUrl, UriKind.Absolute, out var proposed))
            return true;

        return !string.Equals(stored.Scheme, proposed.Scheme, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(stored.IdnHost, proposed.IdnHost, StringComparison.OrdinalIgnoreCase)
               || stored.Port != proposed.Port;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ConnectionSaveResult Fail(string message) => new(false, [message]);
}
