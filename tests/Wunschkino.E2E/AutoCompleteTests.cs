using Microsoft.Playwright;
using Wunschkino.Library;

namespace Wunschkino.E2E;

/// <summary>Auto-Erledigt im echten Browser: ein Wunsch, dessen Titel nach einer Aktualisierung in Jellyfin liegt, wird ohne Zutun erledigt.</summary>
[Collection(BrowserCollection.Name)]
public sealed class AutoCompleteTests(BrowserFixture fixture)
{
    private const string InLibraryTitle = "Der Zauberer vom Südpol";
    private const string NotInLibraryTitle = "Das Geheimnis der blauen Schlucht";

    [Fact]
    public async Task A_wish_for_a_title_that_shows_up_in_jellyfin_is_marked_done_automatically_after_a_refresh()
    {
        await using var jellyfin = await FakeJellyfin.StartAsync();
        using var host = new AppHost(seed: true);
        await host.StartAsync();
        await host.SeedDemoAsync(jellyfin);
        // TMDB-ID 1000 ist die des ersten Titels im Fake-Jellyfin.
        await host.AddOpenWishAsync(1000, MediaKind.Movie, InLibraryTitle, 1998);

        await using var context = await fixture.NewContextAsync(host, Role.Anonymous);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await page.FillAsync("#username", AppHost.AdminName);
        await page.FillAsync("#password", AppHost.Password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync(url => !url.Contains("/login"));

        await page.GotoAsync("/wishes");
        var card = page.Locator(".wish-card").Filter(new() { HasText = InLibraryTitle });
        await Assertions.Expect(card).ToContainTextAsync("Offen");
        await Assertions.Expect(card).Not.ToContainTextAsync("Automatisch");

        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.GetByRole(AriaRole.Button, new() { Name = "Jetzt aktualisieren" }).ClickAsync();

        await WaitForAutoCompletionAsync(page, card);

        await Assertions.Expect(card).ToContainTextAsync("Erledigt");
        await Assertions.Expect(card).ToContainTextAsync("Automatisch");
        var untouched = page.Locator(".wish-card").Filter(new() { HasText = NotInLibraryTitle });
        await Assertions.Expect(untouched).ToContainTextAsync("Offen");
        await Assertions.Expect(untouched).Not.ToContainTextAsync("Automatisch");
    }

    // Die Wunschliste aktualisiert sich nicht von selbst; die Aktualisierung läuft im Hintergrund.
    private static async Task WaitForAutoCompletionAsync(IPage page, ILocator card)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await page.GotoAsync("/wishes");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            if (await card.Locator("text=Automatisch").CountAsync() > 0)
                return;
            await Task.Delay(500);
        }
    }
}
