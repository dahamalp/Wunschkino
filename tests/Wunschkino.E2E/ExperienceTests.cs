using Microsoft.Playwright;

namespace Wunschkino.E2E;

/// <summary>Erlebnis jenseits der Inhalte: Ladegewicht, Tastaturbedienung und Verhalten bei abgebrochener Verbindung.</summary>
[Collection(BrowserCollection.Name)]
public sealed class ExperienceTests(BrowserFixture fixture)
{
    private const long LoginBudgetBytes = 300 * 1024;

    [Fact]
    public async Task Login_page_transfers_less_than_300_KB()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        var page = await context.NewPageAsync();
        var requests = new List<IRequest>();
        page.Request += (_, request) => requests.Add(request);

        await page.GotoAsync("/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        long total = 0;
        foreach (var request in requests)
        {
            var response = await request.ResponseAsync();
            if (response is null)
                continue;
            total += TransferSize(response.Headers.GetValueOrDefault("content-type"), await response.BodyAsync());
        }
        Assert.True(total < LoginBudgetBytes, $"Die Anmeldeseite überträgt {total / 1024} KB (Grenze {LoginBudgetBytes / 1024} KB).");
    }

    [Fact]
    public async Task Skip_link_becomes_visible_on_focus_and_jumps_to_the_content()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/wishes");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var skipLink = page.Locator(".skip-link");
        await skipLink.FocusAsync();
        await Assertions.Expect(skipLink).ToBeFocusedAsync();
        Assert.True((await skipLink.BoundingBoxAsync())!.X >= 0, "Der Link zum Überspringen ist beim Fokus nicht sichtbar.");

        await page.Keyboard.PressAsync("Enter");

        await Assertions.Expect(page.Locator("#main-content")).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Focused_links_show_a_visible_focus_ring()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/wishes");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Mit Tab bis zum ersten Navigationslink: Nur dann gilt :focus-visible wie bei echter Tastaturbedienung.
        for (var i = 0; i < 8 && !await page.EvaluateAsync<bool>("() => document.activeElement?.classList.contains('nav-item') === true"); i++)
            await page.Keyboard.PressAsync("Tab");

        var outline = await page.EvaluateAsync<string>("() => getComputedStyle(document.activeElement).outlineStyle + ' ' + getComputedStyle(document.activeElement).outlineWidth");
        Assert.DoesNotContain("none", outline);
        Assert.DoesNotContain("0px", outline);
    }

    [Fact]
    public async Task Lost_connection_shows_a_german_reconnect_dialog()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/wishes");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await context.SetOfflineAsync(true);

        await Assertions.Expect(page.Locator("#components-reconnect-modal")).ToContainTextAsync("Verbindung", new() { Timeout = 20000 });
    }

    /// <summary>
    /// Was ein veröffentlichter Server überträgt: Der Test-Host liefert Dateien unkomprimiert, ein veröffentlichtes Paket bringt sie
    /// vorkomprimiert mit. Textdateien zählen deshalb gzip-komprimiert (Brotli wäre noch kleiner), Schriften und Bilder in Originalgröße.
    /// </summary>
    private static long TransferSize(string? contentType, byte[] body)
    {
        var isText = contentType is not null
                     && (contentType.StartsWith("text/", StringComparison.Ordinal) || contentType.Contains("javascript") || contentType.Contains("json") || contentType.Contains("svg"));
        if (!isText)
            return body.Length;

        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(body);
        return buffer.Length;
    }

    [Fact]
    public async Task Library_search_without_matches_says_so_instead_of_staying_blank()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.GetByLabel("Suche nach Titel oder Jahr").FillAsync("zzzz-gibt-es-sicher-nicht");

        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Keine Treffer." })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Login_fields_are_reached_in_reading_order_with_the_tab_key()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.FocusAsync("#username");

        var order = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            order.Add(await page.EvaluateAsync<string>("() => document.activeElement.id || document.activeElement.type || document.activeElement.tagName"));
        }

        Assert.Equal(["password", "checkbox", "submit"], order);
    }

    [Theory]
    [InlineData("/", ".library-poster")]
    [InlineData("/wishes", ".wish-poster")]
    public async Task Posters_keep_their_two_to_three_ratio_at_every_width(string path, string selector)
    {
        foreach (var width in new[] { 320, 768, 1280 })
        {
            await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User, new ViewportSize { Width = width, Height = 800 });
            var page = await context.NewPageAsync();
            await page.GotoAsync(path);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var ratios = await page.EvaluateAsync<double[]>(
                "sel => [...document.querySelectorAll(sel)].map(e => e.getBoundingClientRect().height / e.getBoundingClientRect().width)", selector);

            Assert.NotEmpty(ratios);
            Assert.All(ratios, ratio => Assert.InRange(ratio, 1.45, 1.55));
        }
    }
}
