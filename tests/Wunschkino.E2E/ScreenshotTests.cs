using Microsoft.Playwright;

namespace Wunschkino.E2E;

/// <summary>
/// Bestandsaufnahme der Oberfläche: Aufnahmen aller Seiten bei Handy-, Tablet- und
/// Desktop-Breite, hell und dunkel, mit Demo-Daten. Läuft nur, wenn <c>WUNSCHKINO_SCREENSHOTS</c> auf einen Zielordner zeigt.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class ScreenshotTests(BrowserFixture fixture)
{
    private static readonly (string Name, ViewportSize Size)[] Viewports =
    [
        ("handy", new ViewportSize { Width = 360, Height = 740 }),
        ("tablet", new ViewportSize { Width = 768, Height = 1024 }),
        ("desktop", new ViewportSize { Width = 1280, Height = 800 })
    ];

    [Fact]
    public async Task Capture_all_pages()
    {
        var target = Environment.GetEnvironmentVariable("WUNSCHKINO_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(target))
            return;
        Directory.CreateDirectory(target);

        var jobs = Pages.Anonymous.Select(p => (Role.Anonymous, p, fixture.Configured))
            .Concat(Pages.SignedIn.Where(p => p != "/about").Select(p => (Role.User, p, fixture.Configured)))
            .Concat(Pages.AdminOnly.Select(p => (Role.Admin, p, fixture.Configured)))
            .Append((Role.Anonymous, "/setup", fixture.Fresh));

        foreach (var (role, path, host) in jobs)
        foreach (var (viewportName, size) in Viewports)
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await using var context = await fixture.NewContextAsync(host, role, size, scheme);
            var page = await context.NewPageAsync();
            await page.GotoAsync(path);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            var name = (path == "/" ? "start" : path.Trim('/').Replace('/', '_')) + $"-{viewportName}-{(scheme == ColorScheme.Dark ? "dunkel" : "hell")}.png";
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(target, name), FullPage = true });
        }
    }

    [Fact]
    public async Task Capture_open_menus()
    {
        var target = Environment.GetEnvironmentVariable("WUNSCHKINO_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(target))
            return;
        Directory.CreateDirectory(target);

        await using var phone = await fixture.NewContextAsync(fixture.Configured, Role.Admin, Viewports[0].Size);
        var phonePage = await phone.NewPageAsync();
        await phonePage.GotoAsync("/");
        await phonePage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await phonePage.GetByRole(AriaRole.Button, new() { Name = "Menü" }).ClickAsync();
        await phonePage.WaitForTimeoutAsync(500);
        await phonePage.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(target, "menue-handy.png") });

        await using var desktop = await fixture.NewContextAsync(fixture.Configured, Role.Admin, Viewports[2].Size, ColorScheme.Dark);
        var desktopPage = await desktop.NewPageAsync();
        await desktopPage.GotoAsync("/wishes");
        await desktopPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await desktopPage.GetByRole(AriaRole.Button, new() { Name = "Verwaltung" }).ClickAsync();
        await desktopPage.WaitForTimeoutAsync(300);
        await desktopPage.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(target, "menue-desktop-dunkel.png") });
    }

    /// <summary>
    /// Bilder für das README (nur Demo-Daten, eigene Poster-Grafiken, keine Rechte Dritter): <c>WUNSCHKINO_README_SCREENSHOTS</c> zeigt auf
    /// den Zielordner (<c>docs/screenshots</c>). Die Wunschsuche fehlt absichtlich, sie zeigt echte TMDB-Poster.
    /// </summary>
    [Fact]
    public async Task Capture_readme_images()
    {
        var target = Environment.GetEnvironmentVariable("WUNSCHKINO_README_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(target))
            return;
        Directory.CreateDirectory(target);

        await ShootAsync(target, "bibliothek-hell.png", Role.User, "/", Viewports[2].Size, ColorScheme.Light);
        await ShootAsync(target, "bibliothek-dunkel.png", Role.User, "/", Viewports[2].Size, ColorScheme.Dark);
        await ShootAsync(target, "bibliothek-handy.png", Role.User, "/", Viewports[0].Size, ColorScheme.Light);
        await ShootAsync(target, "wunschliste-admin.png", Role.Admin, "/wishes", Viewports[2].Size, ColorScheme.Light);
        await ShootAsync(target, "wunschliste-handy.png", Role.User, "/wishes", Viewports[0].Size, ColorScheme.Dark);
    }

    private async Task ShootAsync(string target, string name, Role role, string path, ViewportSize size, ColorScheme scheme)
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, role, size, scheme);
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForFunctionAsync("() => [...document.images].every(i => i.complete)");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(target, name) });
    }
}
