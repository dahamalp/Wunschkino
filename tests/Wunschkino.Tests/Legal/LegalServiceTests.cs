using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Legal;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Legal;

public class LegalServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AuditLog _audit;
    private readonly LegalService _service;
    private readonly User _admin;
    private readonly User _user;

    public LegalServiceTests()
    {
        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _audit = new AuditLog(_db, TimeProvider.System, NullLogger<AuditLog>.Instance);
        _service = new LegalService(settings, _db, _audit);
        _admin = TestUsers.Add(_db, "admin", UserRole.Admin);
        _user = TestUsers.Add(_db, "nutzer");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Nothing_isConfigured_byDefault()
    {
        var texts = await _service.GetAsync();

        Assert.Equal(LegalTexts.Empty, texts);
    }

    [Fact]
    public async Task SavedTexts_AreReturned_TrimmedAndWithUnixLineEnds()
    {
        var result = await _service.SaveAsync(_admin.Id, new LegalTexts("  Muster GmbH\r\nMusterweg 1  ", "Datenschutz", "post@example.test"));

        Assert.True(result.Success);
        var texts = await _service.GetAsync();
        Assert.Equal("Muster GmbH\nMusterweg 1", texts.Imprint);
        Assert.Equal("Datenschutz", texts.Privacy);
        Assert.Equal("post@example.test", texts.Contact);
    }

    [Fact]
    public async Task BlankFields_AreStoredAsNothing()
    {
        await _service.SaveAsync(_admin.Id, new LegalTexts("Text", null, null));

        await _service.SaveAsync(_admin.Id, new LegalTexts("   ", "", null));

        Assert.Equal(LegalTexts.Empty, await _service.GetAsync());
    }

    [Fact]
    public async Task OnlyActiveAdmins_MaySave()
    {
        var result = await _service.SaveAsync(_user.Id, new LegalTexts("x", null, null));

        Assert.False(result.Success);
        Assert.Equal(LegalTexts.Empty, await _service.GetAsync());
    }

    [Fact]
    public async Task TooLongTexts_AreRejected_AndNothingChanges()
    {
        var tooLong = new string('a', LegalService.MaxTextLength + 1);

        var imprint = await _service.SaveAsync(_admin.Id, new LegalTexts(tooLong, null, null));
        var contact = await _service.SaveAsync(_admin.Id, new LegalTexts(null, null, new string('b', LegalService.MaxContactLength + 1)));

        Assert.False(imprint.Success);
        Assert.False(contact.Success);
        Assert.Equal(LegalTexts.Empty, await _service.GetAsync());
    }

    [Fact]
    public async Task Changes_AreWrittenToTheAuditLog_NamingOnlyTheFieldsNotTheirContent()
    {
        await _service.SaveAsync(_admin.Id, new LegalTexts("Geheimer Impressumstext", null, "kontakt@example.test"));

        var entry = Assert.Single(await _audit.ListAsync());
        Assert.Equal(AuditActions.LegalChanged, entry.Action);
        Assert.Equal("admin", entry.ActorName);
        Assert.Equal("Impressum, Kontakt", entry.Detail);
    }

    [Fact]
    public async Task SavingTheSameTexts_WritesNoAuditEntry()
    {
        await _service.SaveAsync(_admin.Id, new LegalTexts("Text", null, null));

        await _service.SaveAsync(_admin.Id, new LegalTexts("Text", null, null));

        Assert.Single(await _audit.ListAsync());
    }

    [Fact]
    public async Task ANewInstance_ReadsWhatWasSaved()
    {
        await _service.SaveAsync(_admin.Id, new LegalTexts("Impressum", null, null));
        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);

        var fresh = new LegalService(settings, _db);

        Assert.Equal("Impressum", (await fresh.GetAsync()).Imprint);
    }
}
