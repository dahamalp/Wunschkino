using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Wunschkino.Auth;
using Wunschkino.Data;

namespace Wunschkino.E2E;

/// <summary>Neues Konto im echten Browser: Admin-Passwort, Pflicht zur Änderung, Anmeldung mit dem eigenen Passwort.</summary>
[Collection(BrowserCollection.Name)]
public sealed class PasswordChangeTests(BrowserFixture fixture)
{
    private const string Username = "neuling";
    private const string OwnPassword = "mein-eigenes-passwort";

    private async Task CreateUserWithAdminSetPasswordAsync()
    {
        var host = fixture.Configured;
        var dbFactory = host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Username == Username).ExecuteDeleteAsync();
        db.Users.Add(new User
        {
            Username = Username,
            PasswordHash = host.Services.GetRequiredService<IPasswordHasher>().Hash(AppHost.Password),
            MustChangePassword = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task SignInAsync(IPage page, string password)
    {
        await page.FillAsync("#username", Username);
        await page.FillAsync("#password", password);
        await page.ClickAsync("button[type=submit]");
    }

    [Fact]
    public async Task After_changing_the_admin_set_password_the_new_one_signs_in_even_after_a_pause()
    {
        await CreateUserWithAdminSetPasswordAsync();
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await SignInAsync(page, AppHost.Password);
        await page.WaitForURLAsync(url => url.Contains("/account/password"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.GetByLabel("Aktuelles Passwort").FillAsync(AppHost.Password);
        await page.GetByLabel("Neues Passwort", new() { Exact = true }).FillAsync(OwnPassword);
        await page.GetByLabel("Neues Passwort wiederholen").FillAsync(OwnPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Passwort ändern" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/login?passwordChanged=true"), new() { Timeout = 20_000 });

        // Länger als die Merkzeit der Kontoprüfung: So lange braucht man auch zum Eintippen.
        await Task.Delay(PrincipalValidator.CacheLifetime + TimeSpan.FromSeconds(1));
        var response = await page.RunAndWaitForResponseAsync(() => SignInAsync(page, OwnPassword),
            r => r.Request.Method == "POST" && r.Url.Contains("/login"));

        Assert.NotEqual(400, response.Status);
        await page.WaitForURLAsync(url => !url.Contains("/login") && !url.Contains("/account/password"));
    }
}
