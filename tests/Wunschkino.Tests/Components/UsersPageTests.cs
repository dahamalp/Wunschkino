using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;
using UsersPage = Wunschkino.Components.Pages.Admin.Users;

namespace Wunschkino.Tests.Components;

public class UsersPageTests : ComponentTestBase
{
    private const string Password = "ein-sicheres-passwort";

    private readonly TestDb _db;
    private readonly User _admin;

    public UsersPageTests()
    {
        _db = Own(new TestDb());
        _admin = TestUsers.Add(_db, "admin", UserRole.Admin);
        SignInAsAdmin(_admin.Id, "admin");
        Services.AddSingleton(new UserService(_db, new PlainTextHasher(), TimeProvider.System));
        var dataProtection = new EphemeralDataProtectionProvider();
        var settings = new Wunschkino.Services.SettingsService(_db, dataProtection, Microsoft.Extensions.Logging.Abstractions.NullLogger<Wunschkino.Services.SettingsService>.Instance);
        Services.AddSingleton(new TwoFactorService(_db, dataProtection, new PlainTextHasher(), settings, TimeProvider.System));
    }

    private IRenderedComponent<UsersPage> Open()
    {
        var page = RenderPage<UsersPage>();
        page.WaitForAssertion(() => Assert.Contains("admin", page.Markup), Patience);
        return page;
    }

    [Fact]
    public void Users_AreListedWithTheirStatusAsText()
    {
        TestUsers.Add(_db, "gesperrt", disabled: true);

        var page = Open();

        Assert.Contains("Aktiv", page.Markup);
        Assert.Contains("Gesperrt", page.Markup);
        Assert.Contains("gesperrt", page.Markup);
    }

    [Fact]
    public void AccountsWithTwoFactor_AreMarked()
    {
        var anna = TestUsers.Add(_db, "anna");
        using (var db = _db.CreateDbContext())
            db.Users.Where(u => u.Id == anna.Id).ExecuteUpdate(s => s.SetProperty(u => u.TotpEnabledUtc, DateTime.UtcNow));

        var page = Open();

        Assert.Single(page.FindAll(".mud-chip"), chip => chip.TextContent.Contains("Zwei-Faktor-Anmeldung eingeschaltet"));
    }

    [Fact]
    public void TheTable_LabelsEveryCell_ForSmallScreens()
    {
        var page = Open();

        var labels = page.FindAll("td[data-label]").Select(td => td.GetAttribute("data-label")).Distinct().ToList();

        Assert.Contains("Benutzername", labels);
        Assert.Contains("Rolle", labels);
        Assert.Contains("Status", labels);
    }

    private static void FillAdminPassword(IRenderedComponent<UsersPage> page, string password) =>
        page.FindAll("input").Single(i => i.GetAttribute("autocomplete") == "current-password").Change(password);

    private static void FillNewUser(IRenderedComponent<UsersPage> page, string username, string password)
    {
        page.FindAll("input").First(i => i.GetAttribute("type") == "text" && i.GetAttribute("autocomplete") == "off").Change(username);
        page.FindAll("input").Single(i => i.GetAttribute("autocomplete") == "new-password").Change(password);
    }

    [Fact]
    public void ANewUser_CanBeCreated_AndAppearsInTheList()
    {
        var page = Open();
        FillNewUser(page, "neuer", "ein-sicheres-passwort");
        FillAdminPassword(page, "ein-sicheres-passwort");

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Anlegen").Click();

        Snackbars.WaitForAssertion(() => Assert.Contains("Benutzer angelegt.", Snackbars.Markup), Patience);
        page.WaitForAssertion(() => Assert.Contains("neuer", page.Markup), Patience);
        Assert.Equal(2, TestUsersCount());
    }

    [Fact]
    public void ATooShortPassword_IsRejected_WithAnErrorMessage()
    {
        var page = Open();
        FillNewUser(page, "neuer", "kurz");
        FillAdminPassword(page, "ein-sicheres-passwort");

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Anlegen").Click();

        Snackbars.WaitForAssertion(() => Assert.Contains("Zeichen", Snackbars.Markup), Patience);
        Assert.Equal(1, TestUsersCount());
    }

    [Theory]
    [InlineData("")]
    [InlineData("falsches-passwort")]
    public void CreatingAUser_WithoutTheRightAdminPassword_ChangesNothing(string adminPassword)
    {
        var page = Open();
        FillNewUser(page, "neuer", "ein-sicheres-passwort");
        FillAdminPassword(page, adminPassword);

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Anlegen").Click();

        Snackbars.WaitForAssertion(() => Assert.Contains("Das aktuelle Passwort ist falsch.", Snackbars.Markup), Patience);
        Assert.Equal(1, TestUsersCount());
    }

    [Fact]
    public void TheAdminPasswordField_IsClearedAfterEveryAction()
    {
        var page = Open();
        FillNewUser(page, "neuer", "ein-sicheres-passwort");
        FillAdminPassword(page, "ein-sicheres-passwort");

        page.FindAll("button").Single(b => b.TextContent.Trim() == "Anlegen").Click();

        page.WaitForAssertion(() =>
            Assert.Equal("", page.FindAll("input").Single(i => i.GetAttribute("autocomplete") == "current-password").GetAttribute("value") ?? ""), Patience);
    }

    private void ChooseFromMenu(IRenderedComponent<UsersPage> page, string username, string item)
    {
        page.Find($"button[aria-label='Aktionen: {username}']").Click();
        Popovers.WaitForAssertion(() => Assert.Contains(item, Popovers.Markup), Patience);
        Popovers.FindAll(".mud-menu-item").Single(i => i.TextContent.Trim() == item).Click();
    }

    private User Reload(int id) => TestUsers.Reload(_db, id);

    [Fact]
    public void AnAdmin_CanMakeAUserAnAdmin_AndBack_WithThePassword()
    {
        var anna = TestUsers.Add(_db, "anna");
        var page = Open();

        FillAdminPassword(page, Password);
        ChooseFromMenu(page, "anna", "Zum Admin machen");
        Snackbars.WaitForAssertion(() => Assert.Contains("Rolle geändert.", Snackbars.Markup), Patience);
        Assert.Equal(UserRole.Admin, Reload(anna.Id).Role);

        FillAdminPassword(page, Password);
        ChooseFromMenu(page, "anna", "Zum Benutzer machen");
        page.WaitForAssertion(() => Assert.Equal(UserRole.User, Reload(anna.Id).Role), Patience);
    }

    [Fact]
    public void ChangingARole_WithoutThePassword_ChangesNothing()
    {
        var anna = TestUsers.Add(_db, "anna");
        var page = Open();

        ChooseFromMenu(page, "anna", "Zum Admin machen");

        Snackbars.WaitForAssertion(() => Assert.Contains("Das aktuelle Passwort ist falsch.", Snackbars.Markup), Patience);
        Assert.Equal(UserRole.User, Reload(anna.Id).Role);
    }

    [Fact]
    public void AnAdmin_CanDisableAndEnableAnAccount_AndTheStatusShowsIt()
    {
        var anna = TestUsers.Add(_db, "anna");
        var page = Open();

        FillAdminPassword(page, Password);
        ChooseFromMenu(page, "anna", "Konto sperren");
        Snackbars.WaitForAssertion(() => Assert.Contains("Konto gesperrt.", Snackbars.Markup), Patience);
        page.WaitForAssertion(() => Assert.Contains("Gesperrt", page.Markup), Patience);
        Assert.True(Reload(anna.Id).IsDisabled);

        FillAdminPassword(page, Password);
        ChooseFromMenu(page, "anna", "Konto entsperren");
        page.WaitForAssertion(() => Assert.False(Reload(anna.Id).IsDisabled), Patience);
    }

    [Fact]
    public void TheOwnAccount_CannotBeDisabled_AndTheLastAdminStays()
    {
        var page = Open();

        FillAdminPassword(page, Password);
        ChooseFromMenu(page, "admin", "Konto sperren");

        Snackbars.WaitForAssertion(() => Assert.Contains("Das eigene Konto kann nicht gesperrt werden.", Snackbars.Markup), Patience);
        Assert.False(Reload(_admin.Id).IsDisabled);
    }

    [Fact]
    public void ALastAdmin_CannotBeDemoted()
    {
        var page = Open();

        FillAdminPassword(page, Password);
        ChooseFromMenu(page, "admin", "Zum Benutzer machen");

        Snackbars.WaitForAssertion(() => Assert.Contains("mindestens ein aktiver Admin", Snackbars.Markup), Patience);
        Assert.Equal(UserRole.Admin, Reload(_admin.Id).Role);
    }

    [Fact]
    public void ResettingAPassword_NeedsTheAdminPassword_AndMakesTheOwnerPickANewOne()
    {
        var anna = TestUsers.Add(_db, "anna");
        var page = Open();
        ChooseFromMenu(page, "anna", "Passwort zurücksetzen");
        page.WaitForAssertion(() => Assert.Contains("Neues Passwort", page.Markup), Patience);
        // Zwei Felder tragen new-password: „Startpasswort“ beim Anlegen (oben) und „Neues Passwort“ im Zurücksetzen-Feld (danach).
        page.FindAll("input").Last(i => i.GetAttribute("autocomplete") == "new-password").Change("ganz-neues-passwort");

        FillAdminPassword(page, "falsch");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Setzen").Click();
        Snackbars.WaitForAssertion(() => Assert.Contains("Das aktuelle Passwort ist falsch.", Snackbars.Markup), Patience);
        Assert.Equal(anna.PasswordHash, Reload(anna.Id).PasswordHash);

        FillAdminPassword(page, Password);
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Setzen").Click();

        Snackbars.WaitForAssertion(() => Assert.Contains("Passwort gesetzt.", Snackbars.Markup), Patience);
        var after = Reload(anna.Id);
        Assert.NotEqual(anna.PasswordHash, after.PasswordHash);
        Assert.True(after.MustChangePassword);
    }

    [Fact]
    public void ResettingTwoFactor_AsksForConfirmation_AndNeedsThePassword()
    {
        var anna = TestUsers.Add(_db, "anna");
        using (var db = _db.CreateDbContext())
            db.Users.Where(u => u.Id == anna.Id).ExecuteUpdate(s => s.SetProperty(u => u.TotpEnabledUtc, DateTime.UtcNow));
        var page = Open();

        ChooseFromMenu(page, "anna", "Zwei-Faktor-Anmeldung zurücksetzen");
        page.WaitForAssertion(() => Assert.Contains("für „anna“ zurücksetzen?", page.Markup), Patience);
        FillAdminPassword(page, Password);
        page.FindAll("button").Last(b => b.TextContent.Contains("Zwei-Faktor-Anmeldung zurücksetzen")).Click();

        Snackbars.WaitForAssertion(() => Assert.Contains("zurückgesetzt.", Snackbars.Markup), Patience);
        Assert.False(Reload(anna.Id).HasTwoFactor);
    }

    [Fact]
    public void EachRowActionMenu_NamesItsUser_ForScreenReaders()
    {
        TestUsers.Add(_db, "alice");

        var page = Open();

        var labels = page.FindAll("button[aria-label^='Aktionen']").Select(b => b.GetAttribute("aria-label")).ToList();
        Assert.Contains("Aktionen: admin", labels);
        Assert.Contains("Aktionen: alice", labels);
    }

    private int TestUsersCount()
    {
        using var db = _db.CreateDbContext();
        return db.Users.Count();
    }
}
