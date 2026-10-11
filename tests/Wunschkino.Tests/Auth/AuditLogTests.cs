using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class AuditLogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly AuditLog _audit;

    public AuditLogTests() => _audit = new AuditLog(_db, _time, NullLogger<AuditLog>.Instance);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Write_StoresTheEntryWithTimeActorTargetDetailAndAddress()
    {
        await _audit.WriteAsync(AuditActions.Login, 7, "anna", "ziel", "details", "203.0.113.7");

        var entry = Assert.Single(await _audit.ListAsync());
        Assert.Equal(_time.GetUtcNow().UtcDateTime, entry.TimeUtc);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Equal("anna", entry.ActorName);
        Assert.Equal(AuditActions.Login, entry.Action);
        Assert.Equal("ziel", entry.Target);
        Assert.Equal("details", entry.Detail);
        Assert.Equal("203.0.113.7", entry.Ip);
    }

    [Fact]
    public async Task List_CanHideLogins()
    {
        await _audit.WriteAsync(AuditActions.Login, 1, "anna", null);
        await _audit.WriteAsync(AuditActions.UserCreated, 1, "admin", "bob");

        var withoutLogins = await _audit.ListAsync(includeLogins: false);

        Assert.Equal(AuditActions.UserCreated, Assert.Single(withoutLogins).Action);
        Assert.Equal(2, (await _audit.ListAsync()).Count);
    }

    [Fact]
    public async Task List_ShowsTheNewestFirst_AndCapsTheCount()
    {
        for (var i = 0; i < 5; i++)
        {
            await _audit.WriteAsync(AuditActions.UserCreated, null, "admin", $"user{i}");
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var all = await _audit.ListAsync();
        var two = await _audit.ListAsync(2);

        Assert.Equal(["user4", "user3", "user2", "user1", "user0"], all.Select(e => e.Target));
        Assert.Equal(["user4", "user3"], two.Select(e => e.Target));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task List_WithANonPositiveCount_StillReturnsOneEntry(int max)
    {
        await _audit.WriteAsync(AuditActions.Login, null, "anna");
        await _audit.WriteAsync(AuditActions.Login, null, "bert");

        Assert.Single(await _audit.ListAsync(max));
    }

    [Fact]
    public async Task Write_ClipsOverlongTexts_InsteadOfFailing()
    {
        await _audit.WriteAsync(new string('a', 100), null, new string('n', 100), new string('t', 500), new string('d', 1000), new string('i', 200));

        var entry = Assert.Single(await _audit.ListAsync());
        Assert.Equal(48, entry.Action.Length);
        Assert.Equal(32, entry.ActorName!.Length);
        Assert.Equal(100, entry.Target!.Length);
        Assert.Equal(300, entry.Detail!.Length);
        Assert.Equal(64, entry.Ip!.Length);
    }

    [Fact]
    public async Task Write_TurnsBlankOptionalFieldsIntoNull()
    {
        await _audit.WriteAsync(AuditActions.UserRole, null, "  ", "", "   ", " ");

        var entry = Assert.Single(await _audit.ListAsync());
        Assert.Null(entry.ActorName);
        Assert.Null(entry.Target);
        Assert.Null(entry.Detail);
        Assert.Null(entry.Ip);
    }

    [Fact]
    public async Task Write_NeverThrows_WhenTheDatabaseFails_SoTheActionItselfSucceeds()
    {
        var broken = new AuditLog(new BrokenFactory(), _time, NullLogger<AuditLog>.Instance);

        await broken.WriteAsync(AuditActions.Login, 1, "anna");
    }

    [Fact]
    public async Task EntriesOlderThanAYear_AreDeleted_YoungerOnesStay()
    {
        await _audit.WriteAsync("alt", null, "x");
        _time.Advance(AuditLog.Retention + TimeSpan.FromDays(1));
        await _audit.WriteAsync("mittel", null, "x");
        _time.Advance(TimeSpan.FromDays(10));

        await _audit.WriteAsync("neu", null, "x");
        _time.Advance(TimeSpan.FromHours(2));
        await _audit.WriteAsync("neuer", null, "x");

        Assert.Equal(["neuer", "neu", "mittel"], (await _audit.ListAsync()).Select(e => e.Action));
    }

    [Fact]
    public async Task AddressesOlderThan90Days_AreRemoved_ButTheEntryStays()
    {
        await _audit.WriteAsync(AuditActions.Login, 1, "anna", ip: "203.0.113.7");
        _time.Advance(AuditLog.IpRetention + TimeSpan.FromDays(1));
        await _audit.WriteAsync(AuditActions.Login, 1, "anna", ip: "203.0.113.8");

        var entries = await _audit.ListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("203.0.113.8", entries[0].Ip);
        Assert.Null(entries[1].Ip);
    }

    [Fact]
    public async Task TheAddressOfABlockedClient_IsRemovedAfter90Days_JustLikeTheLoginAddress_OtherTargetsStay()
    {
        await _audit.WriteAsync(AuditActions.IpBlocked, null, null, "203.0.113.7", "nach 3 Fehlversuchen");
        await _audit.WriteAsync(AuditActions.IpUnblocked, null, "admin", "203.0.113.7");
        await _audit.WriteAsync(AuditActions.UserCreated, null, "admin", "anna", "User");
        _time.Advance(AuditLog.IpRetention + TimeSpan.FromDays(1));

        await _audit.WriteAsync(AuditActions.IpBlocked, null, null, "203.0.113.8", "nach 3 Fehlversuchen");

        var entries = await _audit.ListAsync();
        Assert.Equal("203.0.113.8", entries[0].Target);
        Assert.All(entries.Where(e => e.Action is AuditActions.IpBlocked or AuditActions.IpUnblocked).Skip(1), e => Assert.Null(e.Target));
        Assert.Equal("anna", entries.Single(e => e.Action == AuditActions.UserCreated).Target);
        Assert.Equal("nach 3 Fehlversuchen", entries.Last(e => e.Action == AuditActions.IpBlocked).Detail);
    }

    [Fact]
    public async Task TheCleanUp_RunsAtMostOncePerHour()
    {
        await _audit.WriteAsync("erster", null, "x");
        await using (var db = _db.CreateDbContext())
        {
            db.AuditEntries.Add(new AuditEntry { Action = "uralt", TimeUtc = _time.GetUtcNow().UtcDateTime - AuditLog.Retention - TimeSpan.FromDays(30) });
            await db.SaveChangesAsync();
        }

        await _audit.WriteAsync("zweiter", null, "x");
        Assert.Contains(await _audit.ListAsync(), e => e.Action == "uralt");

        _time.Advance(TimeSpan.FromMinutes(61));
        await _audit.WriteAsync("dritter", null, "x");

        Assert.DoesNotContain(await _audit.ListAsync(), e => e.Action == "uralt");
    }

    [Fact]
    public void EveryKnownAction_HasAReadableGermanLabel_AndUnknownOnesFallBackToTheKey()
    {
        foreach (var action in new[]
                 {
                     AuditActions.Login, AuditActions.SetupCompleted, AuditActions.UserCreated, AuditActions.UserDisabled,
                     AuditActions.UserEnabled, AuditActions.UserRole, AuditActions.UserPasswordReset, AuditActions.UserUnlocked,
                     AuditActions.PasswordChanged, AuditActions.ConnectionsChanged, AuditActions.WishSettingsChanged,
                     AuditActions.IpBlocked, AuditActions.IpUnblocked, AuditActions.IpUnblockedAll
                 })
        {
            Assert.NotEqual(action, AuditActions.Label(action));
        }

        Assert.Equal("etwas.neues", AuditActions.Label("etwas.neues"));
    }

    private sealed class BrokenFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("Datenbank nicht erreichbar.");
    }
}
