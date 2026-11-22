using Microsoft.Playwright;
using Wunschkino.Library;

namespace Wunschkino.E2E;

/// <summary>Wichtigkeit im echten Browser: Die eigene Stufe lässt sich an der Karte ändern, bleibt gespeichert und fließt in die Sortierung ein.</summary>
[Collection(BrowserCollection.Name)]
public sealed class PriorityTests(BrowserFixture fixture)
{
    private const string FirstTitle = "Der Zauberer vom Südpol";
    private const string SecondTitle = "Das Geheimnis der blauen Schlucht";

    [Fact]
    public async Task The_own_importance_can_be_changed_on_a_wish_card_and_stays_after_a_reload()
    {
        using var host = new AppHost(seed: true);
        await host.StartAsync();
        await host.AddOpenWishAsync(1000, MediaKind.Movie, FirstTitle, 1998);
        await host.AddOpenWishAsync(1001, MediaKind.Movie, SecondTitle, 2001);

        await using var context = await fixture.NewContextAsync(host, Role.Anonymous);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await page.FillAsync("#username", AppHost.UserName);
        await page.FillAsync("#password", AppHost.Password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync(url => !url.Contains("/login"));

        await page.GotoAsync("/wishes");
        var firstCard = page.Locator(".wish-card").Filter(new() { HasText = FirstTitle });
        await Assertions.Expect(firstCard).Not.ToContainTextAsync("besonders wichtig");

        await firstCard.GetByRole(AriaRole.Combobox).ClickAsync();
        await page.Locator(".mud-list-item").Filter(new() { HasText = "Besonders wichtig" }).ClickAsync();
        await Assertions.Expect(firstCard).ToContainTextAsync("1× besonders wichtig");

        await page.ReloadAsync();
        await Assertions.Expect(page.Locator(".wish-card").Filter(new() { HasText = FirstTitle })).ToContainTextAsync("1× besonders wichtig");
        await Assertions.Expect(page.Locator(".wish-card").Filter(new() { HasText = SecondTitle })).Not.ToContainTextAsync("besonders wichtig");

        await page.GotoAsync("/wishes?sort=importance");
        await Assertions.Expect(page.Locator(".wish-card").First).ToContainTextAsync(FirstTitle);
    }
}
