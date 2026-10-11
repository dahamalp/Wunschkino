using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.DatabaseBackup;
using Wunschkino.Hosting;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Hosting;

public class SecurityCheckServiceTests : IDisposable
{
    private static readonly IPAddress Proxy = IPAddress.Parse("10.0.0.5");

    private readonly TempDirectory _dir = new();
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly ProxyWarningState _proxyWarning = new();
    private readonly string _databasePath;
    private readonly string _backupDirectory;

    public SecurityCheckServiceTests()
    {
        _databasePath = Path.Combine(_dir.Path, "daten", "wunschkino.db");
        _backupDirectory = Path.Combine(_dir.Path, "andere-platte", "sicherung");
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=False").Options);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }

    private BackupService CreateBackup(int warnAfterDays = 7, bool sameMount = false, string? databasePath = null)
    {
        var settings = new BackupSettings(databasePath ?? _databasePath, _backupDirectory, 7, warnAfterDays);
        // Die Tests geben die Laufwerke vor, damit das Ergebnis nicht davon abhängt, wo der Temp-Ordner dieses Rechners liegt.
        IReadOnlyList<string> mounts = sameMount ? ["/"] : ["/", Path.GetDirectoryName(_backupDirectory)!.Replace('\\', '/')];
        return new BackupService(settings, _time, NullLogger<BackupService>.Instance, mounts);
    }

    private SecurityCheckService Create(
        WunschkinoOptions? options = null,
        TrustedProxies? proxies = null,
        BackupService? backup = null,
        int warnAfterDays = 7)
    {
        var backupService = backup ?? CreateBackup(warnAfterDays);
        return new SecurityCheckService(
            options ?? new WunschkinoOptions { Domain = "wunsch.example.org" },
            proxies ?? new TrustedProxies([Proxy], []),
            backupService,
            new BackupSettings(_databasePath, _backupDirectory, 7, warnAfterDays),
            new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance),
            _proxyWarning,
            _time,
            _db);
    }

    private static SecurityCheckItem Item(IEnumerable<SecurityCheckItem> items, string title) => items.Single(i => i.Title == title);

    private async Task<IReadOnlyList<SecurityCheckItem>> RunAsync(SecurityCheckService? service = null, bool includeBlocks = true) =>
        await (service ?? Create()).RunAsync(includeBlocks);

    [Fact]
    public async Task AdminsWithoutTwoFactor_AreNamedAsARecommendation()
    {
        TestUsers.Add(_db, "chef", UserRole.Admin);
        TestUsers.Add(_db, "anna");
        TestUsers.Add(_db, "rentner", UserRole.Admin, disabled: true);

        var item = Item(await RunAsync(), "Zwei-Faktor-Anmeldung der Admins");

        Assert.Equal(CheckSeverity.Info, item.Severity);
        Assert.Contains("chef", item.Message);
        Assert.DoesNotContain("anna", item.Message);
        Assert.DoesNotContain("rentner", item.Message);
    }

    [Fact]
    public async Task AdminsWithTwoFactor_AreOk()
    {
        var chef = TestUsers.Add(_db, "chef", UserRole.Admin);
        await using (var db = _db.CreateDbContext())
            await db.Users.Where(u => u.Id == chef.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpEnabledUtc, DateTime.UtcNow));

        Assert.Equal(CheckSeverity.Ok, Item(await RunAsync(), "Zwei-Faktor-Anmeldung der Admins").Severity);
    }

    [Fact]
    public async Task AWellConfiguredInstallation_HasNoWarningsAndNothingCritical()
    {
        var backup = CreateBackup();
        await backup.RunNowAsync();

        var items = await RunAsync(Create(backup: backup));

        Assert.DoesNotContain(items, i => i.Severity is CheckSeverity.Warning or CheckSeverity.Critical);
        Assert.All(items.Where(i => i.Title != "Schlüsselordner"), i => Assert.Equal(CheckSeverity.Ok, i.Severity));
    }

    [Fact]
    public async Task TheProxy_NoneListedAndTheAppOpenToTheNetwork_IsAWarning()
    {
        var item = Item(await RunAsync(Create(new WunschkinoOptions { Domain = "x.example.org" }, TrustedProxies.None)), "Reverse-Proxy");

        Assert.Equal(CheckSeverity.Warning, item.Severity);
        Assert.Contains("--proxy", item.Message);
        Assert.Contains("--proxy-mode local", item.Message);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public async Task TheProxy_NoneListedButTheAppOnlyListensLocally_IsFine(string bind)
    {
        var item = Item(await RunAsync(Create(new WunschkinoOptions { Domain = "x", BindAddress = bind }, TrustedProxies.None)), "Reverse-Proxy");

        Assert.Equal(CheckSeverity.Ok, item.Severity);
    }

    [Fact]
    public async Task TheProxy_Listed_IsShownByAddress()
    {
        var proxies = new TrustedProxies([Proxy], [IPNetwork.Parse("192.168.1.0/24")]);

        var item = Item(await RunAsync(Create(proxies: proxies)), "Reverse-Proxy");

        Assert.Equal(CheckSeverity.Ok, item.Severity);
        Assert.Contains("10.0.0.5", item.Message);
        Assert.Contains("192.168.1.0/24", item.Message);
    }

    [Fact]
    public async Task AnObservedUntrustedProxy_IsCritical_NamesTheAddress_AndFadesAfter24Hours()
    {
        _proxyWarning.Record(IPAddress.Parse("203.0.113.9"), _time.GetUtcNow().UtcDateTime);
        var service = Create();

        var critical = Item(await RunAsync(service), "Reverse-Proxy");
        Assert.Equal(CheckSeverity.Critical, critical.Severity);
        Assert.Contains("203.0.113.9", critical.Message);

        _time.Advance(SecurityCheckService.ProxyWarningLifetime + TimeSpan.FromMinutes(1));
        Assert.NotEqual(CheckSeverity.Critical, Item(await RunAsync(service), "Reverse-Proxy").Severity);
    }

    [Fact]
    public async Task ALoopbackProxyWithoutForwardedFor_IsCritical_ExplainsTheFix_AndFadesAfter24Hours()
    {
        _proxyWarning.RecordLoopbackWithoutForwarding(_time.GetUtcNow().UtcDateTime);
        var service = Create();

        var critical = Item(await RunAsync(service), "Reverse-Proxy");
        Assert.Equal(CheckSeverity.Critical, critical.Severity);
        Assert.Contains("X-Forwarded-For", critical.Message);

        _time.Advance(SecurityCheckService.ProxyWarningLifetime + TimeSpan.FromMinutes(1));
        Assert.NotEqual(CheckSeverity.Critical, Item(await RunAsync(service), "Reverse-Proxy").Severity);
    }

    [Fact]
    public async Task DismissingTheProxyNotice_AlsoClearsTheLoopbackFinding()
    {
        _proxyWarning.RecordLoopbackWithoutForwarding(_time.GetUtcNow().UtcDateTime);
        _proxyWarning.Clear();

        Assert.NotEqual(CheckSeverity.Critical, Item(await RunAsync(Create()), "Reverse-Proxy").Severity);
    }

    [Theory]
    [InlineData(null, CheckSeverity.Warning)]
    [InlineData("", CheckSeverity.Warning)]
    [InlineData("   ", CheckSeverity.Warning)]
    [InlineData("wunsch.example.org", CheckSeverity.Ok)]
    public async Task TheDomain_MustBeSet(string? domain, CheckSeverity expected)
    {
        var item = Item(await RunAsync(Create(new WunschkinoOptions { Domain = domain })), "Domain");

        Assert.Equal(expected, item.Severity);
    }

    [Theory]
    [InlineData(true, CheckSeverity.Ok)]
    [InlineData(false, CheckSeverity.Warning)]
    public async Task SecureCookies_AreAWarningWhenSwitchedOff(bool secure, CheckSeverity expected)
    {
        var item = Item(await RunAsync(Create(new WunschkinoOptions { Domain = "x", SecureCookies = secure })), "Sichere Cookies");

        Assert.Equal(expected, item.Severity);
    }

    [Theory]
    [InlineData(1500, CheckSeverity.Ok)]
    [InlineData(1, CheckSeverity.Ok)]
    [InlineData(0, CheckSeverity.Warning)]
    public async Task TheRequestLimit_SwitchedOffIsAWarning(int limit, CheckSeverity expected)
    {
        var item = Item(await RunAsync(Create(new WunschkinoOptions { Domain = "x", RequestLimitPerMinute = limit })), "Anfragen-Limit");

        Assert.Equal(expected, item.Severity);
    }

    [Fact]
    public async Task Backup_NoneYet_IsAWarning_NotCritical()
    {
        var item = Item(await RunAsync(), "Datensicherung");

        Assert.Equal(CheckSeverity.Warning, item.Severity);
        Assert.Contains("noch keine Sicherung", item.Message);
    }

    [Fact]
    public async Task Backup_AFailedRun_IsCritical_AndShowsTheReason()
    {
        var failing = CreateBackup(databasePath: Path.Combine(_dir.Path, "gibtsnicht.db"));
        await failing.RunNowAsync();

        var item = Item(await RunAsync(Create(backup: failing)), "Datensicherung");

        Assert.Equal(CheckSeverity.Critical, item.Severity);
        Assert.Contains("fehlgeschlagen", item.Message);
    }

    [Fact]
    public async Task Backup_TooOld_IsCritical_AndNamesTheAgeAndTheLimit()
    {
        var backup = CreateBackup(warnAfterDays: 3);
        await backup.RunNowAsync();
        _time.Advance(TimeSpan.FromDays(5));

        var item = Item(await RunAsync(Create(backup: backup, warnAfterDays: 3)), "Datensicherung");

        Assert.Equal(CheckSeverity.Critical, item.Severity);
        Assert.Contains("5 Tage", item.Message);
        Assert.Contains("3 Tagen", item.Message);
    }

    [Fact]
    public async Task Backup_OnTheSameDriveAsTheDatabase_IsAWarning()
    {
        var backup = CreateBackup(sameMount: true);
        await backup.RunNowAsync();

        var item = Item(await RunAsync(Create(backup: backup)), "Datensicherung");

        Assert.Equal(CheckSeverity.Warning, item.Severity);
        Assert.Contains("demselben Laufwerk", item.Message);
        Assert.Contains("BackupDirectory", item.Message);
    }

    [Fact]
    public async Task Backup_InAFolderOthersCanRead_IsAWarning_WithTheChmodCommand()
    {
        // Unix-Dateirechte gibt es unter Windows nicht; dort entfällt die Prüfung (auch im Dienst).
        if (OperatingSystem.IsWindows())
            return;

        var backup = CreateBackup();
        await backup.RunNowAsync();
        File.SetUnixFileMode(_backupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var item = Item(await RunAsync(Create(backup: backup)), "Datensicherung");

        Assert.Equal(CheckSeverity.Warning, item.Severity);
        Assert.Contains($"chmod 700 {_backupDirectory}", item.Message);
    }

    [Fact]
    public async Task Backup_Fine_ReportsTimeAndCount_AndLinksToTheBackupSection()
    {
        var backup = CreateBackup();
        await backup.RunNowAsync();

        var item = Item(await RunAsync(Create(backup: backup)), "Datensicherung");

        Assert.Equal(CheckSeverity.Ok, item.Severity);
        Assert.Contains("1 Sicherung", item.Message);
        Assert.Equal("/admin/security#sicherung", item.Link);
    }

    [Fact]
    public async Task TheKeyFolderHint_IsAlwaysShown_AndNeverASeverityProblem()
    {
        var item = Item(await RunAsync(), "Schlüsselordner");

        Assert.Equal(CheckSeverity.Info, item.Severity);
        Assert.Contains("keys", item.Message);
    }

    [Fact]
    public async Task BlockedAddresses_NoneIsFine_TemporaryIsAnInfo_PermanentIsAWarning()
    {
        var service = Create();
        Assert.Equal(CheckSeverity.Ok, Item(await RunAsync(service), "Gesperrte Adressen").Severity);

        var blocks = new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance);
        for (var i = 0; i < 3; i++)
            await blocks.RegisterFailureAsync(IPAddress.Parse("203.0.113.1"), IPAddress.Parse("192.168.1.5"));
        var temporary = Item(await RunAsync(service), "Gesperrte Adressen");
        Assert.Equal(CheckSeverity.Info, temporary.Severity);
        Assert.Equal("/admin/blocks", temporary.Link);

        await using (var db = _db.CreateDbContext())
        {
            db.IpBlocks.Add(new IpBlock { Address = "203.0.113.2", Failures = 18, IsPermanent = true, LastFailureUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var permanent = Item(await RunAsync(service), "Gesperrte Adressen");
        Assert.Equal(CheckSeverity.Warning, permanent.Severity);
        Assert.Contains("dauerhaft", permanent.Message);
    }

    [Fact]
    public async Task WithoutTheBlockCount_NoDatabaseQueryIsNeeded_AndTheItemIsMissing()
    {
        var items = await RunAsync(includeBlocks: false);

        Assert.DoesNotContain(items, i => i.Title == "Gesperrte Adressen");
        Assert.Contains(items, i => i.Title == "Datensicherung");
    }

    [Theory]
    [InlineData(CheckSeverity.Ok, "In Ordnung")]
    [InlineData(CheckSeverity.Info, "Hinweis")]
    [InlineData(CheckSeverity.Warning, "Warnung")]
    [InlineData(CheckSeverity.Critical, "Kritisch")]
    public void EverySeverity_HasATextLabel_SoColorIsNeverTheOnlySignal(CheckSeverity severity, string label)
    {
        Assert.Equal(label, new SecurityCheckItem("t", severity, "m").SeverityLabel);
    }

    [Fact]
    public void TheProxyWarningState_RemembersTheLastSenderOnlyWithinTheWindow()
    {
        var state = new ProxyWarningState();
        var now = _time.GetUtcNow().UtcDateTime;
        Assert.Null(state.LastSeenSince(now.AddDays(-1)));

        state.Record(IPAddress.Parse("203.0.113.9"), now);

        Assert.Equal(IPAddress.Parse("203.0.113.9"), state.LastSeenSince(now.AddMinutes(-1))!.Value.Remote);
        Assert.Null(state.LastSeenSince(now.AddMinutes(1)));
    }
}
