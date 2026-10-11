using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Auth;
using Wunschkino.Data;

namespace Wunschkino.Hosting;

/// <summary>Liest ein Passwort, ohne dass es als Argument (und damit in der Prozessliste und im Shell-Verlauf) auftaucht.</summary>
public interface IPasswordPrompt
{
    /// <summary>Wahr, wenn ein Mensch am Terminal tippt (dann wird zur Kontrolle ein zweites Mal gefragt).</summary>
    bool IsInteractive { get; }

    string? Read(string label);
}

/// <summary>Am Terminal ohne Echo, sonst eine Zeile aus der Standardeingabe (für Skripte: <c>printf '%s\n' "$pw" | …</c>).</summary>
public sealed class ConsolePasswordPrompt : IPasswordPrompt
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string? Read(string label)
    {
        if (!IsInteractive)
            return Console.In.ReadLine()?.TrimEnd('\r');

        Console.Error.Write(label);
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                    typed.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return typed.ToString();
    }
}

/// <summary>
/// Konsolenbefehle zur Benutzerverwaltung für den Fall, dass niemand mehr in die Weboberfläche kommt (etwa nach einem vergessenen
/// Admin-Passwort). Arbeiten direkt auf der Datenbank und schreiben ins Protokoll.
/// </summary>
public static class UserCommands
{
    public const string Usage =
        "Benutzer verwalten:\n" +
        "  Wunschkino --list-users                 zeigt alle Benutzer mit Rolle und Zustand\n" +
        "  Wunschkino --reset-password <Benutzer>  setzt ein neues Passwort (wird abgefragt, nicht als Argument übergeben)\n" +
        "  Wunschkino --reset-2fa <Benutzer>       schaltet die Zwei-Faktor-Anmeldung ab (Handy verloren, keine Codes mehr)";

    public static bool IsUserCommand(string[] args) => args.Any(arg => arg is "--list-users" or "--reset-password" or "--reset-2fa");

    public static async Task<int> RunAsync(
        string[] args,
        string databasePath,
        IPasswordPrompt prompt,
        TextWriter output,
        TextWriter error,
        TimeProvider? time = null,
        IPasswordHasher? hasher = null,
        string? operatorName = null)
    {
        if (!File.Exists(databasePath))
        {
            await error.WriteLineAsync($"Keine Datenbank unter {databasePath} gefunden. Stimmt der Datenordner (Wunschkino:DataDirectory)?");
            return 1;
        }

        var clock = time ?? TimeProvider.System;
        var factory = new FileDbFactory(databasePath);
        try
        {
            if (args.Contains("--list-users"))
                return await ListAsync(factory, clock, output);

            var command = args.Contains("--reset-2fa") ? "--reset-2fa" : "--reset-password";
            var index = Array.IndexOf(args, command);
            if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                await error.WriteLineAsync($"Bei {command} fehlt der Benutzername.\n" + Usage);
                return 2;
            }

            var actor = BlockCommands.ConsoleActorFor(operatorName);
            return command == "--reset-2fa"
                ? await ResetTwoFactorAsync(args[index + 1].Trim(), factory, output, error, clock, actor)
                : await ResetPasswordAsync(args[index + 1].Trim(), factory, prompt, output, error, clock, hasher ?? new Argon2IdPasswordHasher(), actor);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync("Die Datenbank ist noch nicht auf dem neuesten Stand. Starte die App einmal, sie aktualisiert sie selbst.");
            return 1;
        }
    }

    private static async Task<int> ListAsync(FileDbFactory factory, TimeProvider clock, TextWriter output)
    {
        await using var db = factory.CreateDbContext();
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();
        if (users.Count == 0)
        {
            await output.WriteLineAsync("Es gibt noch keine Benutzer. Schließe zuerst die Einrichtung in der Weboberfläche ab.");
            return 0;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await output.WriteLineAsync($"{"Benutzer",-34} {"Rolle",-8} {"2FA",-5} Zustand");
        foreach (var user in users)
        {
            var state = user.IsDisabled ? "gesperrt (deaktiviert)"
                : user.LockedUntilUtc > now ? $"vorübergehend gesperrt bis {user.LockedUntilUtc:yyyy-MM-dd HH:mm} UTC"
                : "aktiv";
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{user.Username,-34} {(user.Role == UserRole.Admin ? "Admin" : "Nutzer"),-8} {(user.HasTwoFactor ? "an" : "aus"),-5} {state}"));
        }
        return 0;
    }

    private static async Task<int> ResetPasswordAsync(
        string username,
        FileDbFactory factory,
        IPasswordPrompt prompt,
        TextWriter output,
        TextWriter error,
        TimeProvider clock,
        IPasswordHasher hasher,
        string actor)
    {
        await using var db = factory.CreateDbContext();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username);
        if (user is null)
        {
            await error.WriteLineAsync($"Den Benutzer „{username}“ gibt es nicht. --list-users zeigt alle.");
            return 1;
        }

        var password = prompt.Read($"Neues Passwort für {user.Username}: ");
        if (string.IsNullOrEmpty(password))
        {
            await error.WriteLineAsync("Kein Passwort eingegeben, nichts geändert.");
            return 1;
        }
        if (UserValidation.ValidatePassword(password) is { } problem)
        {
            await error.WriteLineAsync($"{problem} Nichts geändert.");
            return 1;
        }
        if (prompt.IsInteractive && prompt.Read("Passwort wiederholen: ") != password)
        {
            await error.WriteLineAsync("Die beiden Eingaben stimmen nicht überein, nichts geändert.");
            return 1;
        }

        user.PasswordHash = hasher.Hash(password);
        user.SecurityStamp = User.NewSecurityStamp();
        user.FailedLogins = 0;
        user.LockedUntilUtc = null;
        await db.SaveChangesAsync();

        var audited = await new AuditLog(factory, clock, NullLogger<AuditLog>.Instance)
            .WriteAsync(AuditActions.UserPasswordReset, null, actor, user.Username);

        await output.WriteLineAsync($"Das Passwort von {user.Username} ist geändert. Alle bisherigen Anmeldungen dieses Kontos sind abgemeldet.");
        if (!audited)
            await error.WriteLineAsync("Warnung: Die Änderung konnte nicht ins Protokoll geschrieben werden.");
        if (user.IsDisabled)
            await output.WriteLineAsync("Hinweis: Das Konto ist gesperrt (deaktiviert). Ein Admin kann es in der Weboberfläche freigeben.");
        return 0;
    }

    private static async Task<int> ResetTwoFactorAsync(string username, FileDbFactory factory, TextWriter output, TextWriter error, TimeProvider clock, string actor)
    {
        await using var db = factory.CreateDbContext();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Username == username);
        if (user is null)
        {
            await error.WriteLineAsync($"Den Benutzer „{username}“ gibt es nicht. --list-users zeigt alle.");
            return 1;
        }
        if (!user.HasTwoFactor)
        {
            await output.WriteLineAsync($"Für {user.Username} ist keine Zwei-Faktor-Anmeldung eingeschaltet, nichts geändert.");
            return 0;
        }

        await TwoFactorService.ClearAsync(db, user.Id, rotateSecurityStamp: true, CancellationToken.None);
        var audited = await new AuditLog(factory, clock, NullLogger<AuditLog>.Instance)
            .WriteAsync(AuditActions.TwoFactorReset, null, actor, user.Username);

        await output.WriteLineAsync($"Die Zwei-Faktor-Anmeldung von {user.Username} ist abgeschaltet. Alle bisherigen Anmeldungen dieses Kontos sind abgemeldet.");
        if (!audited)
            await error.WriteLineAsync("Warnung: Die Änderung konnte nicht ins Protokoll geschrieben werden.");
        return 0;
    }

    private sealed class FileDbFactory(string databasePath) : IDbContextFactory<AppDbContext>
    {
        // Mode=ReadWrite legt keine neue Datenbank an, Pooling=False schließt jeden Zugriff selbst (siehe BlockCommands).
        public AppDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Mode=ReadWrite;Pooling=False").Options);
    }
}
