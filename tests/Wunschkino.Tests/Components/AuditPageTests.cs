using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;
using AuditPage = Wunschkino.Components.Pages.Admin.Audit;

namespace Wunschkino.Tests.Components;

public class AuditPageTests : ComponentTestBase
{
    private readonly TestDb _db;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly AuditLog _audit;

    public AuditPageTests()
    {
        _db = Own(new TestDb());
        _audit = new AuditLog(_db, _time, NullLogger<AuditLog>.Instance);
        SignInAsAdmin();
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(_db);
        Services.AddSingleton(_audit);
    }

    [Fact]
    public void WithoutEntries_SaysSo_AndExplainsWhereFailedLoginsAre()
    {
        var page = RenderPage<AuditPage>();

        page.WaitForAssertion(() => Assert.Contains("Es gibt noch keine Einträge", page.Markup));
        Assert.Contains("journalctl -u wunschkino", page.Markup);
        Assert.Contains("365 Tage", page.Markup);
        Assert.Contains("90 Tage", page.Markup);
    }

    [Fact]
    public async Task ListsTheEntriesNewestFirst_WithReadableActionsAndAllColumns()
    {
        await _audit.WriteAsync(AuditActions.Login, 1, "anna", ip: "203.0.113.7");
        _time.Advance(TimeSpan.FromMinutes(5));
        await _audit.WriteAsync(AuditActions.UserCreated, 1, "admin", "bert", "User");
        _time.Advance(TimeSpan.FromMinutes(5));
        await _audit.WriteAsync(AuditActions.IpBlocked, null, null, "198.51.100.9", "nach 3 Fehlversuchen, bis 2026-10-04 12:11 UTC");

        var page = RenderPage<AuditPage>();

        page.WaitForAssertion(() => Assert.Contains("Adresse gesperrt", page.Markup));
        var markup = page.Markup;
        Assert.True(markup.IndexOf("Adresse gesperrt", StringComparison.Ordinal) < markup.IndexOf("Benutzer angelegt", StringComparison.Ordinal));
        Assert.True(markup.IndexOf("Benutzer angelegt", StringComparison.Ordinal) < markup.IndexOf("Angemeldet", StringComparison.Ordinal));
        Assert.Contains("System", markup);
        Assert.Contains("bert", markup);
        Assert.Contains("nach 3 Fehlversuchen", markup);
        Assert.Contains("203.0.113.7", markup);
    }

    [Fact]
    public async Task Times_AreShownInTheBrowsersTimeZone_EvenWhenItIsReportedAfterRendering()
    {
        await _audit.WriteAsync(AuditActions.UserCreated, 1, "admin", "bert");
        var zone = Services.GetRequiredService<Wunschkino.Localization.UserTimeZone>();
        var page = RenderPage<AuditPage>();
        page.WaitForAssertion(() => Assert.Contains("bert", page.Markup));

        zone.TrySet("Asia/Tokyo");

        page.WaitForAssertion(() => Assert.Contains("21:00:00", page.Find("tbody td").TextContent));
    }

    [Fact]
    public async Task TextsFromUsers_AreShownEncoded_NotAsMarkup()
    {
        await _audit.WriteAsync(AuditActions.UserCreated, 1, "admin", "<script>alert(1)</script>", "<b>fett</b>");

        var page = RenderPage<AuditPage>();

        page.WaitForAssertion(() => Assert.Contains("&lt;script&gt;", page.Markup));
        Assert.DoesNotContain("<script>alert(1)</script>", page.Markup);
        Assert.DoesNotContain("<b>fett</b>", page.Markup);
    }

    [Fact]
    public async Task OnlyTheLatest200Entries_AreListed()
    {
        for (var i = 0; i < AuditLog.MaxListed + 5; i++)
            await _audit.WriteAsync(AuditActions.UserCreated, null, "admin", $"user{i:D3}");

        var page = RenderPage<AuditPage>();

        page.WaitForAssertion(() => Assert.Contains($"user{AuditLog.MaxListed + 4:D3}", page.Markup));
        Assert.DoesNotContain("user000", page.Markup);
        Assert.Equal(AuditLog.MaxListed, page.FindAll("tbody tr").Count);
    }
}
