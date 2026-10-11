using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;
using BlocksPage = Wunschkino.Components.Pages.Admin.Blocks;

namespace Wunschkino.Tests.Components;

public class BlocksPageTests : ComponentTestBase
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly IpBlockService _blocks;
    private readonly AuditLog _audit;

    public BlocksPageTests()
    {
        _db = Own(new TestDb());
        _audit = new AuditLog(_db, _time, NullLogger<AuditLog>.Instance);
        _blocks = new IpBlockService(_db, _time, Wunschkino.TrustedProxies.None, NullLogger<IpBlockService>.Instance, _audit);
        var admin = TestUsers.Add(_db, "admin", UserRole.Admin);
        SignInAsAdmin(admin.Id);
        Services.AddSingleton(new AdminGuard(_db));
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(_db);
        Services.AddSingleton(_blocks);
        Services.AddSingleton<TimeProvider>(_time);
    }

    private void Add(string address, int failures, DateTime? until = null, bool permanent = false)
    {
        using var db = _db.CreateDbContext();
        db.IpBlocks.Add(new IpBlock
        {
            Address = address, Failures = failures, BlockedUntilUtc = until, IsPermanent = permanent, LastFailureUtc = Now.AddMinutes(-5)
        });
        db.SaveChanges();
    }

    private int Count()
    {
        using var db = _db.CreateDbContext();
        return db.IpBlocks.Count();
    }

    [Fact]
    public void WithoutAnyBlock_SaysSo()
    {
        var page = RenderPage<BlocksPage>();

        page.WaitForAssertion(() => Assert.Contains("Keine Adresse hat Fehlversuche", page.Markup));
        Assert.DoesNotContain("Alle entsperren", page.Markup);
    }

    [Fact]
    public void ListsEveryAddress_WithItsState()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(5));
        Add("203.0.113.2", 18, permanent: true);
        Add("203.0.113.3", 2);

        var page = RenderPage<BlocksPage>();

        page.WaitForAssertion(() => Assert.Contains("203.0.113.1", page.Markup));
        Assert.Contains("Gesperrt bis", page.Markup);
        Assert.Contains("Dauerhaft gesperrt", page.Markup);
        Assert.Contains("Nicht gesperrt", page.Markup);
    }

    [Fact]
    public void TheHint_ExplainsTheConsoleCommandForLockedOutAdmins()
    {
        var page = RenderPage<BlocksPage>();

        page.WaitForAssertion(() => Assert.Contains("--unblock", page.Markup));
        Assert.Contains("--unblock-all", page.Markup);
    }

    [Fact]
    public void Unblock_RemovesOnlyThatAddress_AndUpdatesTheList()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(5));
        Add("203.0.113.2", 18, permanent: true);
        var page = RenderPage<BlocksPage>();
        page.WaitForAssertion(() => Assert.Contains("203.0.113.2", page.Markup));

        page.Find("button[aria-label='203.0.113.2 entsperren']").Click();

        page.WaitForAssertion(() => Assert.DoesNotContain("203.0.113.2", page.Markup));
        Assert.Contains("203.0.113.1", page.Markup);
        Assert.Equal(1, Count());
    }

    [Fact]
    public async Task Unblocking_IsWrittenToTheAuditLog_WithTheAdminAsActor()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(5));
        Add("203.0.113.2", 18, permanent: true);
        var page = RenderPage<BlocksPage>();
        page.WaitForAssertion(() => Assert.Contains("203.0.113.2", page.Markup));

        page.Find("button[aria-label='203.0.113.2 entsperren']").Click();
        page.WaitForAssertion(() => Assert.DoesNotContain("203.0.113.2", page.Markup));
        page.FindAll("button").Single(b => b.TextContent.Contains("Alle entsperren")).Click();
        Dialogs.WaitForAssertion(() => Assert.Contains("Wirklich alle", Dialogs.Markup), Patience);
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Alle entsperren")).Click();
        page.WaitForAssertion(() => Assert.Contains("Keine Adresse hat Fehlversuche", page.Markup));

        var entries = await _audit.ListAsync();
        Assert.Equal([AuditActions.IpUnblockedAll, AuditActions.IpUnblocked], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Equal("admin", e.ActorName));
        Assert.Equal("203.0.113.2", entries[1].Target);
    }

    [Fact]
    public void UnblockAll_EmptiesTheList()
    {
        Add("203.0.113.1", 3, Now.AddMinutes(5));
        Add("203.0.113.2", 18, permanent: true);
        var page = RenderPage<BlocksPage>();
        page.WaitForAssertion(() => Assert.Contains("Alle entsperren", page.Markup));

        page.FindAll("button").Single(b => b.TextContent.Contains("Alle entsperren")).Click();
        Dialogs.WaitForAssertion(() => Assert.Contains("Wirklich alle", Dialogs.Markup), Patience);
        Dialogs.FindAll("button").Single(b => b.TextContent.Contains("Alle entsperren")).Click();

        page.WaitForAssertion(() => Assert.Contains("Keine Adresse hat Fehlversuche", page.Markup));
        Assert.Equal(0, Count());
    }
}
