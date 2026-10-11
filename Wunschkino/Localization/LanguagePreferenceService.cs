using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;
using Wunschkino.Services;

namespace Wunschkino.Localization;

/// <summary>
/// Sprache pro Konto (in der DB, nicht im Browser) und die Standardsprache des Admins. Die Sprache wird bei jeder Anfrage
/// gebraucht, deshalb liegen beide Werte im Speicher und werden nur beim Ändern neu geschrieben.
/// </summary>
public sealed class LanguagePreferenceService(IDbContextFactory<AppDbContext> dbFactory, SettingsService settings)
{
    private readonly ConcurrentDictionary<int, string?> _users = new();
    private volatile string? _defaultLanguage;

    /// <returns>Der gewählte Code oder null für „automatisch (Browser)“.</returns>
    public async Task<string?> GetAsync(int userId, CancellationToken ct = default)
    {
        if (_users.TryGetValue(userId, out var cached))
            return cached;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Language).SingleOrDefaultAsync(ct);
        var language = AppLanguages.Normalize(stored);
        // GetOrAdd statt Zuweisung: Hat ein gleichzeitiges SetAsync schon den neuen Wert eingetragen, bleibt er stehen.
        return _users.GetOrAdd(userId, language);
    }

    public async Task SetAsync(int userId, string? language, CancellationToken ct = default)
    {
        var normalized = AppLanguages.Normalize(language);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var updated = await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, normalized), ct);
        if (updated == 1)
            _users[userId] = normalized;
    }

    public async Task<string> GetDefaultAsync(CancellationToken ct = default)
    {
        if (_defaultLanguage is { } cached)
            return cached;
        var language = AppLanguages.Normalize(await settings.GetAsync(SettingKeys.DefaultLanguage, ct)) ?? AppLanguages.Default;
        _defaultLanguage = language;
        return language;
    }

    public async Task SetDefaultAsync(string language, CancellationToken ct = default)
    {
        var normalized = AppLanguages.Normalize(language) ?? AppLanguages.Default;
        await settings.SetAsync(SettingKeys.DefaultLanguage, normalized, isProtected: false, ct);
        _defaultLanguage = normalized;
    }
}
