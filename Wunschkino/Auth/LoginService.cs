using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;
using Wunschkino.Resources;

namespace Wunschkino.Auth;

public sealed record LoginResult(ClaimsPrincipal? Principal, string? Error, PendingSecondFactor? Pending = null)
{
    /// <summary>Die Anmeldung gelang mit einem Wiederherstellungscode; die Oberfläche weist dann auf die übrigen Codes hin.</summary>
    public bool UsedRecoveryCode { get; init; }

    public bool Success => Principal is not null;

    public bool RequiresSecondFactor => Pending is not null;
}

/// <summary>Das Passwort stimmt, es fehlt noch der Code. Der Stempel bindet den Zwischenstand an den Stand des Kontos.</summary>
public sealed record PendingSecondFactor(int UserId, string Username, string SecurityStamp);

public sealed class LoginService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPasswordHasher passwordHasher,
    TimeProvider time,
    TwoFactorService? twoFactor = null)
{
    public static string WrongSecondFactor => Texts.Msg_Login_WrongCode;

    // Bewusst eine einzige Meldung für alle Fehlerfälle, damit nicht erkennbar ist, ob ein Konto existiert oder gesperrt ist.
    public static string GenericError => Texts.Msg_Login_Failed;

    private readonly Lazy<string> _dummyHash = new(() => passwordHasher.Hash(Guid.NewGuid().ToString()));

    public async Task<LoginResult> AuthenticateAsync(string? username, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || password.Length > UserValidation.MaxPasswordLength)
            return Fail();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == username.Trim(), ct);
        var now = time.GetUtcNow().UtcDateTime;

        if (user is null || user.IsDisabled || LoginThrottle.IsLocked(user, now))
        {
            // Gleicher Rechenaufwand wie bei einem prüfbaren Konto, damit die Antwortzeit nichts verrät.
            passwordHasher.Verify(password, _dummyHash.Value);
            return Fail();
        }

        // Fehlversuche sperren bewusst nicht das Konto: Wer einen Benutzernamen kennt, könnte sonst jeden, auch den einzigen
        // Admin, aussperren. Den Schutz übernimmt die Sperre der Absender-Adresse (LoginGuard, IpBlockService).
        if (!passwordHasher.Verify(password, user.PasswordHash))
            return Fail();

        if (user.HasTwoFactor)
            return new LoginResult(null, null, new PendingSecondFactor(user.Id, user.Username, user.SecurityStamp));

        await LoginThrottle.ResetAsync(db, user.Id, ct);
        return new LoginResult(AuthClaims.CreatePrincipal(user, time.GetUtcNow()), null);
    }

    /// <summary>Zweiter Schritt: Gilt nur, solange Konto und Stempel unverändert sind (kein Passwortwechsel, keine Sperre dazwischen).</summary>
    public async Task<(LoginResult Result, SecondFactorKind Kind)> CompleteSecondFactorAsync(
        PendingSecondFactor pending, string? code, CancellationToken ct = default)
    {
        if (twoFactor is null)
            return (Fail(), SecondFactorKind.None);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == pending.UserId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (user is null || user.IsDisabled || !user.HasTwoFactor || user.SecurityStamp != pending.SecurityStamp
            || LoginThrottle.IsLocked(user, now))
            return (Fail(), SecondFactorKind.None);
        if (SecondFactorThrottle.IsLocked(user, now))
            return (new LoginResult(null, Texts.Msg_Login_TooManyCodes), SecondFactorKind.None);

        var kind = await twoFactor.VerifyAsync(user.Id, code, ct);
        if (kind == SecondFactorKind.None)
        {
            await SecondFactorThrottle.RegisterFailureAsync(db, user.Id, now, ct);
            return (new LoginResult(null, WrongSecondFactor), kind);
        }

        await SecondFactorThrottle.ResetAsync(db, user.Id, ct);
        await LoginThrottle.ResetAsync(db, user.Id, ct);
        return (new LoginResult(AuthClaims.CreatePrincipal(user, time.GetUtcNow()), null), kind);
    }

    private static LoginResult Fail() => new(null, GenericError);
}
