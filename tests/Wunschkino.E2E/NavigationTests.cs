using Microsoft.Playwright;

namespace Wunschkino.E2E;

[Collection(BrowserCollection.Name)]
public sealed class NavigationTests(BrowserFixture fixture)
{
    private static readonly ViewportSize Phone = new() { Width = 360, Height = 740 };

    [Theory]
    [InlineData(Role.User, "/")]
    [InlineData(Role.User, "/wishes")]
    [InlineData(Role.Admin, "/")]
    [InlineData(Role.Admin, "/admin/users")]
    [InlineData(Role.Admin, "/admin/settings")]
    public async Task Phone_pages_do_not_scroll_sideways(Role role, string path)
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, role, Phone);
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");

        Assert.True(overflow <= 0, $"{path} ist auf dem Handy {overflow}px zu breit.");
    }

    [Fact]
    public async Task Phone_menu_opens_lists_admin_links_and_closes_after_navigation()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Admin, Phone);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.GetByRole(AriaRole.Button, new() { Name = "Menü" }).ClickAsync();
        var drawer = page.Locator(".app-drawer");
        await Assertions.Expect(drawer.GetByRole(AriaRole.Link, new() { Name = "Wünsche" })).ToBeVisibleAsync();
        await Assertions.Expect(drawer.GetByRole(AriaRole.Link, new() { Name = "Einstellungen" })).ToBeVisibleAsync();

        await drawer.GetByRole(AriaRole.Link, new() { Name = "Wünsche" }).ClickAsync();

        await page.WaitForURLAsync("**/wishes");
        await Assertions.Expect(page.Locator(".app-drawer.mud-drawer--open")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Desktop_marks_the_current_page_and_offers_admin_menu()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Admin);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/wishes");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Hauptnavigation" }).First;
        await Assertions.Expect(nav.Locator("a[aria-current=page]")).ToHaveTextAsync("Wünsche");

        await nav.GetByRole(AriaRole.Button, new() { Name = "Verwaltung" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Menuitem, new() { Name = "Protokoll" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Regular_users_get_no_admin_entries()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Equal(0, await page.GetByText("Verwaltung").CountAsync());
        Assert.Equal(0, await page.GetByText("Einstellungen").CountAsync());
    }

    [Fact]
    public async Task Chosen_theme_is_kept_per_account_after_reload()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User, scheme: ColorScheme.Light);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal("rgba(246,246,247,1)", await Background(page));

        await page.GetByRole(AriaRole.Button, new() { Name = "Darstellung" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Dunkel" }).ClickAsync();
        await page.WaitForFunctionAsync("() => getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background').trim() === 'rgba(18,18,20,1)'");

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.Equal("rgba(18,18,20,1)", await Background(page));

        await page.GetByRole(AriaRole.Button, new() { Name = "Darstellung" }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "System" }).ClickAsync();
        await page.WaitForFunctionAsync("() => getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background').trim() === 'rgba(246,246,247,1)'");
    }

    // Übersetzungen sind oft länger als das Deutsche; jede Sprache muss auf das kleinste Handy passen. Geprüft wird bei 300 statt 320 px:
    // Auf Linux (CI) sind die Schriftmaße etwas größer als unter Windows, die Reserve fängt das ab.
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("es-ES")]
    [InlineData("nl-NL")]
    public async Task No_page_scrolls_sideways_on_a_small_phone(string locale)
    {
        var narrow = new ViewportSize { Width = 300, Height = 640 };
        var offenders = new List<string>();
        var jobs = Pages.Anonymous.Select(p => (Role.Anonymous, p))
            .Concat(Pages.SignedIn.Select(p => (Role.User, p)))
            .Concat(Pages.AdminOnly.Select(p => (Role.Admin, p)));

        foreach (var (role, path) in jobs)
        {
            await using var context = await fixture.NewContextAsync(fixture.Configured, role, narrow, locale: locale);
            var page = await context.NewPageAsync();
            await page.GotoAsync(path);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
            if (overflow > 0)
                offenders.Add($"{path} (+{overflow}px)");
        }

        Assert.True(offenders.Count == 0, $"Zu breit bei 300 px ({locale}): " + string.Join(", ", offenders));
    }

    // Die Kopfzeile schneidet Überstehendes ab, statt zu scrollen: Der Test auf seitliches Scrollen allein merkt das nicht.
    [Theory]
    [InlineData(Role.User, 320)]
    [InlineData(Role.User, 360)]
    [InlineData(Role.Admin, 320)]
    [InlineData(Role.Admin, 360)]
    public async Task Every_header_control_stays_inside_a_small_phone_screen(Role role, int width)
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, role, new ViewportSize { Width = width, Height = 640 });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var outside = await page.EvaluateAsync<string[]>(
            """
            (width) => [...document.querySelectorAll('.app-bar a, .app-bar button')]
                .filter(e => e.offsetParent !== null)
                .map(e => ({ name: e.getAttribute('aria-label') || e.textContent.trim(), box: e.getBoundingClientRect() }))
                .filter(x => x.box.width > 0 && (x.box.left < -0.5 || x.box.right > width + 0.5))
                .map(x => `${x.name} (${Math.round(x.box.left)}..${Math.round(x.box.right)})`)
            """,
            width);

        Assert.True(outside.Length == 0, $"Bei {width} px ragen aus dem Bild: " + string.Join(", ", outside));
    }

    [Theory]
    [InlineData(ColorScheme.Dark, "rgba(18,18,20,1)")]
    [InlineData(ColorScheme.Light, "rgba(246,246,247,1)")]
    public async Task Login_page_follows_the_system_color_scheme_without_a_connection(ColorScheme scheme, string expectedBackground)
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous, scheme: scheme);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/login");

        Assert.Equal(expectedBackground, await Background(page));
    }

    private static async Task<string> Background(IPage page) =>
        (await page.EvaluateAsync<string>("getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-background')")).Trim();
}
