using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Auth;
using Wunschkino.Data;

namespace Wunschkino.Hosting;

/// <summary>
/// Konsolenbefehle für den Betreiber auf dem Server: Gesperrte Adressen auflisten und entsperren, auch wenn niemand mehr in die
/// Weboberfläche kommt (etwa weil der einzige Admin selbst gesperrt ist). Arbeitet direkt auf der Datenbank; die laufende App
/// übernimmt eine Entsperrung innerhalb weniger Sekunden.
/// </summary>
public static class BlockCommands
{
    public const string Usage =
        "Gesperrte Adressen verwalten:\n" +
        "  Wunschkino --list-blocks            zeigt alle Adressen mit Fehlversuchen und Sperren\n" +
        "  Wunschkino --unblock <Adresse>      entsperrt eine Adresse (IPv4 oder IPv6)\n" +
        "  Wunschkino --unblock-all            entsperrt alle Adressen";

    /// <summary>So steht eine Entsperrung per Konsole im Protokoll.</summary>
    public const string ConsoleActor = "Konsole";

    /// <summary>
    /// Hängt den Namen des angemeldeten Systembenutzers an (<c>SUDO_USER</c>), damit im Protokoll steht, wer per sudo am Server war.
    /// Nur Buchstaben, Ziffern und <c>._-</c> bleiben erhalten; der Name stammt aus der Umgebung und landet in einer Tabelle.
    /// </summary>
    public static string ConsoleActorFor(string? operatorName)
    {
        var clean = new string((operatorName ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').Take(20).ToArray());
        return clean.Length == 0 ? ConsoleActor : $"{ConsoleActor} ({clean})";
    }

    public static bool IsBlockCommand(string[] args) =>
        args.Any(arg => arg is "--list-blocks" or "--unblock" or "--unblock-all");

    public static async Task<int> RunAsync(
        string[] args, string databasePath, TextWriter output, TextWriter error, TimeProvider? time = null, string? operatorName = null)
    {
        if (!File.Exists(databasePath))
        {
            await error.WriteLineAsync($"Keine Datenbank unter {databasePath} gefunden. Stimmt der Datenordner (Wunschkino:DataDirectory)?");
            return 1;
        }

        var factory = new FileDbFactory(databasePath);
        var audit = new AuditLog(factory, time ?? TimeProvider.System, NullLogger<AuditLog>.Instance);
        var service = new IpBlockService(
            factory, time ?? TimeProvider.System, TrustedProxies.None, NullLogger<IpBlockService>.Instance, audit);
        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var actor = ConsoleActorFor(operatorName);

        try
        {
            if (args.Contains("--list-blocks"))
                return await ListAsync(service, now, output);

            if (args.Contains("--unblock-all"))
            {
                var count = await service.UnblockAllAsync(actor);
                await output.WriteLineAsync(count == 1 ? "1 Adresse entsperrt." : $"{count} Adressen entsperrt.");
                return 0;
            }

            var index = Array.IndexOf(args, "--unblock");
            if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                await error.WriteLineAsync("Bei --unblock fehlt die Adresse.\n" + Usage);
                return 2;
            }

            var address = args[index + 1];
            if (await service.UnblockAsync(address, actor))
            {
                await output.WriteLineAsync($"{IpBlockService.NormalizeKey(address)} entsperrt.");
                return 0;
            }

            await error.WriteLineAsync($"{IpBlockService.NormalizeKey(address)} steht nicht in der Liste. Die Liste zeigt --list-blocks.");
            return 1;
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync("Die Datenbank ist noch nicht auf dem neuesten Stand. Starte die App einmal, sie aktualisiert sie selbst.");
            return 1;
        }
    }

    private static async Task<int> ListAsync(IpBlockService service, DateTime now, TextWriter output)
    {
        var blocks = await service.ListAsync();
        if (blocks.Count == 0)
        {
            await output.WriteLineAsync("Keine Adresse hat Fehlversuche oder ist gesperrt.");
            return 0;
        }

        await output.WriteLineAsync($"{"Adresse",-42} {"Zustand",-36} {"Fehlversuche",12}  Letzter Fehlversuch (UTC)");
        foreach (var block in blocks)
        {
            var state = block.IsPermanent ? "dauerhaft gesperrt"
                : block.BlockedUntilUtc > now ? $"gesperrt bis {block.BlockedUntilUtc:yyyy-MM-dd HH:mm} UTC"
                : "nicht gesperrt";
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{block.Address,-42} {state,-36} {block.Failures,12}  {block.LastFailureUtc:yyyy-MM-dd HH:mm}"));
        }
        return 0;
    }

    private sealed class FileDbFactory(string databasePath) : IDbContextFactory<AppDbContext>
    {
        // Mode=ReadWrite legt keine neue Datenbank an, falls der Pfad falsch ist. Ohne Pool schließt jeder Zugriff seine Verbindung
        // selbst, ein prozessweites Leeren der Pools (das fremde Verbindungen träfe) ist dadurch nicht nötig.
        public AppDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Mode=ReadWrite;Pooling=False").Options);
    }
}
