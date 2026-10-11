using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Wunschkino.Auth;
using Wunschkino.Data;

namespace Wunschkino.E2E;

/// <summary>Zwei-Faktor-Anmeldung im echten Browser: einschalten, Wiederherstellungscodes sehen, mit Code anmelden.</summary>
[Collection(BrowserCollection.Name)]
public sealed class TwoFactorTests(BrowserFixture fixture)
{
    private const string Username = "zweifaktor";

    private async Task CreateUserAsync()
    {
        var host = fixture.Configured;
        var dbFactory = host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Users.AnyAsync(u => u.Username == Username))
            return;
        db.Users.Add(new User
        {
            Username = Username,
            PasswordHash = host.Services.GetRequiredService<IPasswordHasher>().Hash(AppHost.Password),
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task SignInWithPasswordAsync(IPage page)
    {
        await page.GotoAsync("/login");
        await page.FillAsync("#username", Username);
        await page.FillAsync("#password", AppHost.Password);
        await page.ClickAsync("button[type=submit]");
    }

    [Fact]
    public async Task Turning_it_on_shows_recovery_codes_and_the_next_login_asks_for_a_code()
    {
        await CreateUserAsync();
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        var page = await context.NewPageAsync();
        await SignInWithPasswordAsync(page);
        await page.WaitForURLAsync(url => !url.Contains("/login"));

        await page.GotoAsync("/account/2fa");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.GetByRole(AriaRole.Button, new() { Name = "Einrichten" }).ClickAsync();
        var secretText = await page.Locator(".totp-secret code").InnerTextAsync();
        var secret = Totp.FromBase32(secretText)!;
        await page.GetByLabel("Aktuelles Passwort zur Bestätigung").FillAsync(AppHost.Password);
        await page.GetByLabel("Sechsstelliger Code aus der App").FillAsync(Totp.ComputeCode(secret, Totp.TimeStep(DateTimeOffset.UtcNow)));
        await page.GetByRole(AriaRole.Button, new() { Name = "2. Code prüfen und einschalten" }).ClickAsync();

        var codes = page.Locator(".recovery-codes li");
        // Das Einschalten prüft das Passwort mit Argon2; auf einem langsamen CI-Runner reichen die üblichen 5 Sekunden nicht immer.
        await Assertions.Expect(codes).ToHaveCountAsync(TwoFactorService.RecoveryCodeCount, new() { Timeout = 20_000 });
        var recoveryCode = await codes.First.InnerTextAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Ich habe die Codes gesichert" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Die Zwei-Faktor-Anmeldung ist eingeschaltet. Bei jeder Anmeldung")).ToBeVisibleAsync();

        await using var second = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        var loginPage = await second.NewPageAsync();
        await SignInWithPasswordAsync(loginPage);
        await loginPage.WaitForURLAsync(url => url.Contains("/login/2fa"));
        await loginPage.FillAsync("#code", recoveryCode);
        await loginPage.ClickAsync("button[type=submit]");
        await loginPage.WaitForURLAsync(url => !url.Contains("/login"));

        var cookies = (await second.CookiesAsync()).Select(c => c.Name).ToList();
        Assert.Contains("Wunschkino.Auth", cookies);
        Assert.DoesNotContain(TwoFactorTicket.CookieName, cookies);
    }
}
