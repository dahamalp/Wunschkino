using System.Text.RegularExpressions;
using Wunschkino.Resources;

namespace Wunschkino.Auth;

public static partial class UserValidation
{
    public const int MinPasswordLength = 12;

    // Obergrenze, damit niemand mit riesigen Passwörtern teure Argon2-Läufe erzwingt.
    public const int MaxPasswordLength = 128;

    public static string? ValidateUsername(string? username) =>
        username is not null && UsernamePattern().IsMatch(username.Trim())
            ? null
            : Texts.Msg_User_UsernameRules;

    public static string? ValidatePassword(string? password)
    {
        if (password is not { Length: >= MinPasswordLength and <= MaxPasswordLength })
            return string.Format(Texts.Culture, Texts.Msg_User_PasswordLength, MinPasswordLength, MaxPasswordLength);
        if (CommonPasswords.Enabled && CommonPasswords.Contains(password))
            return Texts.Msg_User_PasswordCommon;
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$")]
    private static partial Regex UsernamePattern();
}
