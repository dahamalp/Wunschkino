using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Wunschkino.Auth;
using Wunschkino.Data;

namespace Wunschkino.E2E;

/// <summary>Sprachwahl im Kopfmenü: gilt sofort, bleibt beim Konto und lässt sich auf „automatisch“ zurückstellen.</summary>
[Collection(BrowserCollection.Name)]
public sealed class LanguageTests(BrowserFixture fixture)
{
    private const string Username = "sprache";

    private async Task<string?> StoredLanguageAsync()
    {
        var dbFactory = fixture.Configured.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.Where(u => u.Username == Username).Select(u => u.Language).SingleAsync();
    }

    private async Task<IPage> SignInAsync(IBrowserContext context)
    {
        var host = fixture.Configured;
        var dbFactory = host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (!await db.Users.AnyAsync(u => u.Username == Username))
            {
                db.Users.Add(new User
                {
                    Username = Username,
                    PasswordHash = host.Services.GetRequiredService<IPasswordHasher>().Hash(AppHost.Password),
                    CreatedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
        }

        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await page.FillAsync("#username", Username);
        await page.FillAsync("#password", AppHost.Password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync(url => !url.Contains("/login"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    private static async Task ChooseAsync(IPage page, Func<ILocator, ILocator> item)
    {
        await page.Locator(".language-menu button").First.ClickAsync();
        await item(page.GetByRole(AriaRole.Menuitem)).ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    [Fact]
    public async Task Choosing_a_language_switches_the_interface_and_is_kept_for_the_account()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        var page = await SignInAsync(context);
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "de");

        await ChooseAsync(page, items => items.Filter(new() { HasText = "Nederlands" }));
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "nl");
        Assert.Equal("nl", await StoredLanguageAsync());

        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "nl");

        // „Automatisch“ ist der erste Eintrag; danach gilt wieder die Sprache des Browsers (de-DE).
        await ChooseAsync(page, items => items.First);
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "de");
        Assert.Null(await StoredLanguageAsync());
    }
}
