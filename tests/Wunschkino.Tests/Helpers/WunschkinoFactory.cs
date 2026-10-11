using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Setup;

namespace Wunschkino.Tests.Helpers;

/// <summary>Startet die echte App-Pipeline mit eigenem Temp-Datenordner und einem bereits eingerichteten Admin.</summary>
public sealed class WunschkinoFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "ein-sicheres-passwort";

    private readonly TempDirectory _dataDirectory = new();

    /// <summary>Zusätzliche Einstellungen (zum Beispiel <c>Wunschkino:Domain</c>); müssen vor der ersten Anfrage gesetzt sein.</summary>
    public Dictionary<string, string> ExtraSettings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Wunschkino:DataDirectory", _dataDirectory.Path);
        // Kein gemeinsamer Pool: ClearAllPools() einer anderen Testklasse träfe sonst die Verbindungen dieser App mitten im Zugriff.
        builder.UseSetting("Wunschkino:SqlitePooling", "false");
        foreach (var (key, value) in ExtraSettings)
            builder.UseSetting(key, value);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestRemoteAddressStartupFilter>());
    }

    /// <summary>
    /// Der TestServer kennt keine Absender-Adresse. Mit den Headern <c>X-Test-Remote-Ip</c> und <c>X-Test-Local-Ip</c> setzt
    /// ein Test sie, bevor die App-Pipeline (mit ForwardedHeaders und Rate-Limit) läuft – so verhält sich die Anfrage, als käme
    /// sie von dieser Adresse über das Netz.
    /// </summary>
    private sealed class TestRemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Remote-Ip"].ToString(), out var remote))
                    context.Connection.RemoteIpAddress = remote;
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Local-Ip"].ToString(), out var local))
                    context.Connection.LocalIpAddress = local;
                await nextMiddleware();
            });
            next(app);
        };
    }

    public async Task SeedAdminAsync()
    {
        var dbFactory = Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Users.AnyAsync())
            return;

        db.Users.Add(new User
        {
            Username = AdminUsername,
            PasswordHash = Services.GetRequiredService<IPasswordHasher>().Hash(AdminPassword),
            Role = UserRole.Admin
        });
        await db.SaveChangesAsync();
        Services.GetRequiredService<SetupState>().MarkCompleted();
    }

    public HttpClient CreateNonRedirectingClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _dataDirectory.Dispose();
        }
    }
}
