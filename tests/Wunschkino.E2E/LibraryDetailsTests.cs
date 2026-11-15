using Microsoft.Playwright;

namespace Wunschkino.E2E;

/// <summary>Detailansicht der Bibliothek im echten Browser: Karte anklicken, Daten und Staffeln sehen, wieder schließen.</summary>
[Collection(BrowserCollection.Name)]
public sealed class LibraryDetailsTests(BrowserFixture fixture)
{
    private async Task<IPage> OpenAsync(string path)
    {
        var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    [Fact]
    public async Task Clicking_a_series_shows_its_details_and_seasons_and_the_back_button_closes_it()
    {
        var page = await OpenAsync("/");

        await page.GetByRole(AriaRole.Button, new() { Name = "Die Archivare" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Worum es in „Die Archivare“ geht.");
        await Assertions.Expect(dialog).ToContainTextAsync("Anna Beispiel");
        await Assertions.Expect(dialog).ToContainTextAsync("Staffel 2 · 1 Folge");
        Assert.Contains("item=s01", page.Url);

        await dialog.GetByText("Staffel 1 · 2 Folgen").ClickAsync();
        await Assertions.Expect(dialog).ToContainTextAsync("Staub und Papier");

        await page.GoBackAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        Assert.DoesNotContain("item=", page.Url);
        await page.Context.CloseAsync();
    }

    [Fact]
    public async Task A_link_opens_a_movie_directly_and_escape_closes_it()
    {
        var page = await OpenAsync("/?item=m01");

        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Der Zauberer vom Südpol");
        await Assertions.Expect(dialog).ToContainTextAsync("112 Min.");
        await Assertions.Expect(dialog).ToContainTextAsync("Regina Regie");

        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        await page.WaitForURLAsync(url => !url.Contains("item="));
        await page.Context.CloseAsync();
    }

    [Fact]
    public async Task A_double_click_opens_one_dialog_and_closing_it_returns_focus_to_the_card()
    {
        var page = await OpenAsync("/");
        var card = page.GetByRole(AriaRole.Button, new() { Name = "Nachtzug nach Nirgendwo" });

        await card.DblClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync("Regina Regie");
        await Assertions.Expect(dialog).ToHaveCountAsync(1);

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Schließen", Exact = true }).Last.ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        await page.WaitForURLAsync(url => !url.Contains("item="));
        Assert.EndsWith("/", new Uri(page.Url).AbsolutePath);
        await Assertions.Expect(card).ToBeFocusedAsync();
        await page.Context.CloseAsync();
    }
}
