using Microsoft.EntityFrameworkCore;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Resources;
using Wunschkino.Services;

namespace Wunschkino.Legal;

/// <summary>Vom Betreiber gepflegte Rechtstexte. Alle Felder sind reiner Text (kein HTML) und optional.</summary>
public sealed record LegalTexts(string? Imprint, string? Privacy, string? Contact)
{
    public static readonly LegalTexts Empty = new(null, null, null);
}

public sealed record LegalSaveResult(bool Success, string? Error)
{
    public static LegalSaveResult Ok() => new(true, null);
    public static LegalSaveResult Fail(string error) => new(false, error);
}

/// <summary>
/// Impressum, Datenschutzerklärung und Kontakt des Betreibers. Die Texte stehen in jeder Fußzeile und werden bei jedem Seitenaufruf
/// gebraucht; deshalb liegt der letzte Stand im Speicher. Laden und Speichern laufen unter einer Sperre, damit ein langsamer Leser
/// nie einen neueren Stand überschreibt, und das Speichern ist eine einzige Transaktion.
/// </summary>
public sealed class LegalService(SettingsService settings, IDbContextFactory<AppDbContext> dbFactory, AuditLog? audit = null)
{
    public const int MaxTextLength = 8000;
    public const int MaxContactLength = 300;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LegalTexts? _cached;

    /// <summary>Wird nach jedem erfolgreichen Speichern ausgelöst, damit offene Sitzungen ihre Fußzeile neu aufbauen.</summary>
    public event Action? Changed;

    public async Task<LegalTexts> GetAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _cached) is { } cached)
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            return _cached ??= await LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LegalSaveResult> SaveAsync(int actingUserId, LegalTexts update, CancellationToken ct = default)
    {
        string? actorName;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (!await AdminCheck.IsActiveAdminAsync(db, actingUserId, ct))
                return LegalSaveResult.Fail(Texts.Msg_User_NotAllowed);
            actorName = await db.Users.AsNoTracking().Where(u => u.Id == actingUserId).Select(u => u.Username).SingleOrDefaultAsync(ct);
        }

        var imprint = Normalize(update.Imprint);
        var privacy = Normalize(update.Privacy);
        var contact = Normalize(update.Contact);
        if (imprint?.Length > MaxTextLength || privacy?.Length > MaxTextLength)
            return LegalSaveResult.Fail(string.Format(Texts.Culture, Texts.Msg_Legal_TextTooLong, MaxTextLength));
        if (contact?.Length > MaxContactLength)
            return LegalSaveResult.Fail(string.Format(Texts.Culture, Texts.Msg_Legal_ContactTooLong, MaxContactLength));

        LegalTexts before;
        await _gate.WaitAsync(ct);
        try
        {
            before = _cached ?? await LoadAsync(ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await settings.StageAsync(db, SettingKeys.LegalImprint, imprint, isProtected: false, ct);
            await settings.StageAsync(db, SettingKeys.LegalPrivacy, privacy, isProtected: false, ct);
            await settings.StageAsync(db, SettingKeys.LegalContact, contact, isProtected: false, ct);
            await db.SaveChangesAsync(ct);
            _cached = new LegalTexts(imprint, privacy, contact);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();

        if (audit is not null)
        {
            var changed = new List<string>();
            if (before.Imprint != imprint)
                changed.Add("Impressum");
            if (before.Privacy != privacy)
                changed.Add("Datenschutzerklärung");
            if (before.Contact != contact)
                changed.Add("Kontakt");
            if (changed.Count > 0)
                await audit.WriteAsync(AuditActions.LegalChanged, actingUserId, actorName, detail: string.Join(", ", changed), ct: ct);
        }
        return LegalSaveResult.Ok();
    }

    private async Task<LegalTexts> LoadAsync(CancellationToken ct) => new(
        Blank(await settings.GetAsync(SettingKeys.LegalImprint, ct)),
        Blank(await settings.GetAsync(SettingKeys.LegalPrivacy, ct)),
        Blank(await settings.GetAsync(SettingKeys.LegalContact, ct)));

    /// <summary>Vereinheitlicht Zeilenenden und entfernt Leerraum an den Rändern; leere Texte werden zu null.</summary>
    private static string? Normalize(string? value) =>
        Blank(value?.Replace("\r\n", "\n").Replace('\r', '\n'));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
