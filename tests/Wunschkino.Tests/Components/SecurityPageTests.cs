using System.Net;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.DatabaseBackup;
using Wunschkino.Hosting;
using Wunschkino.Tests.Helpers;
using SecurityBanner = Wunschkino.Components.Layout.SecurityBanner;
using SecurityPage = Wunschkino.Components.Pages.Admin.Security;

namespace Wunschkino.Tests.Components;

public class SecurityPageTests : ComponentTestBase
{
    private readonly TestDb _db;
    private readonly TempDirectory _dir;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly ProxyWarningState _proxyWarning = new();
    private readonly BackupService _backup;
    private readonly string _backupDirectory;

    public SecurityPageTests()
    {
        _db = Own(new TestDb());
        _dir = Own(new TempDirectory());
        var databasePath = Path.Combine(_dir.Path, "daten", "wunschkino.db");
        _backupDirectory = Path.Combine(_dir.Path, "andere-platte", "sicherung");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options))
            db.Database.Migrate();

        var settings = new BackupSettings(databasePath, _backupDirectory, 7, 7);
        _backup = new BackupService(settings, _time, NullLogger<BackupService>.Instance, ["/", Path.GetDirectoryName(_backupDirectory)!.Replace('\\', '/')]);

        SignInAsAdmin();
        Services.AddSingleton(_backup);
        Services.AddSingleton(settings);
        Services.AddSingleton(_proxyWarning);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton(new WunschkinoOptions { Domain = "wunsch.example.org" });
        Services.AddSingleton(new TrustedProxies([IPAddress.Parse("10.0.0.5")], []));
        Services.AddSingleton(new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance));
        Services.AddSingleton<SecurityCheckService>();
    }

    [Fact]
    public void ShowsEveryCheck_WithATextLabelForItsState_AndTheBackupSection()
    {
        var page = RenderPage<SecurityPage>();

        page.WaitForAssertion(() => Assert.Contains("Datensicherung", page.Markup));
        foreach (var title in new[] { "Reverse-Proxy", "Domain", "Sichere Cookies", "Anfragen-Limit", "Datensicherung", "Schlüsselordner", "Gesperrte Adressen" })
            Assert.Contains(title, page.Markup);
        Assert.Contains("In Ordnung", page.Markup);
        Assert.Contains("Warnung", page.Markup);
        Assert.Contains("noch keine Sicherung", page.Markup);
        Assert.Contains(_backupDirectory, page.Markup);
        Assert.Contains("nie der Schlüsselordner", page.Markup);
    }

    [Fact]
    public void BackupNow_CreatesABackup_AndTheSectionShowsIt()
    {
        var page = RenderPage<SecurityPage>();
        page.WaitForAssertion(() => Assert.Contains("noch keine Sicherung", page.Markup));

        page.FindAll("button").Single(b => b.TextContent.Contains("Jetzt sichern")).Click();

        page.WaitForAssertion(() => Assert.Contains("1 Sicherung", page.Markup), Patience);
        Assert.DoesNotContain("noch keine Sicherung", page.Markup);
        Assert.Single(Directory.GetFiles(_backupDirectory, "wunschkino-*.db"));
    }

    [Fact]
    public void ACriticalCheck_IsShownAsCritical_WithItsReason()
    {
        _proxyWarning.Record(IPAddress.Parse("203.0.113.9"), _time.GetUtcNow().UtcDateTime);

        var page = RenderPage<SecurityPage>();

        page.WaitForAssertion(() => Assert.Contains("Kritisch", page.Markup));
        Assert.Contains("203.0.113.9", page.Markup);
    }

    [Fact]
    public void TheProxyWarning_CanBeDismissed()
    {
        _proxyWarning.Record(IPAddress.Parse("203.0.113.9"), _time.GetUtcNow().UtcDateTime);
        var page = RenderPage<SecurityPage>();
        page.WaitForAssertion(() => Assert.Contains("203.0.113.9", page.Markup));

        page.FindAll("button").Single(b => b.TextContent.Contains("Hinweis verwerfen")).Click();

        page.WaitForAssertion(() => Assert.DoesNotContain("203.0.113.9", page.Markup));
        Assert.DoesNotContain("Hinweis verwerfen", page.Markup);
    }

    [Fact]
    public void TheBanner_IsHidden_WhenNothingIsCritical()
    {
        var banner = Render<SecurityBanner>();

        banner.WaitForAssertion(() => Assert.DoesNotContain("Achtung", banner.Markup));
        Assert.Empty(banner.FindAll("[role=alert]"));
    }

    [Fact]
    public void TheBanner_ShowsTheCriticalReason_AndLinksToTheSecurityCheck()
    {
        _proxyWarning.Record(IPAddress.Parse("203.0.113.9"), _time.GetUtcNow().UtcDateTime);

        var banner = Render<SecurityBanner>();

        banner.WaitForAssertion(() => Assert.Contains("Achtung", banner.Markup));
        Assert.Contains("203.0.113.9", banner.Markup);
        Assert.Contains("/admin/security", banner.Find("a").GetAttribute("href"));
        Assert.Single(banner.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task TheBanner_MentionsFurtherCriticalPoints()
    {
        _proxyWarning.Record(IPAddress.Parse("203.0.113.9"), _time.GetUtcNow().UtcDateTime);
        await _backup.RunNowAsync();
        _time.Advance(TimeSpan.FromDays(10));
        _proxyWarning.Record(IPAddress.Parse("203.0.113.9"), _time.GetUtcNow().UtcDateTime);

        var banner = Render<SecurityBanner>();

        banner.WaitForAssertion(() => Assert.Contains("und 1 weiterer kritischer Punkt", banner.Markup));
    }
}
