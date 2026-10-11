using Microsoft.Playwright;
using Wunschkino.Auth;

namespace Wunschkino.E2E;

/// <summary>Grundstruktur jeder Seite (WCAG 2.1: Überschriften und Landmarken) und Browser-Speicher.</summary>
[Collection(BrowserCollection.Name)]
public sealed class StructureTests(BrowserFixture fixture)
{
    [Theory]
    [MemberData(nameof(Pages.AnonymousData), MemberType = typeof(Pages))]
    public Task Anonymous_pages(string path) => AssertStructureAsync(fixture.Configured, Role.Anonymous, path);

    [Theory]
    [MemberData(nameof(Pages.SignedInData), MemberType = typeof(Pages))]
    public Task Signed_in_pages(string path) => AssertStructureAsync(fixture.Configured, Role.User, path);

    [Theory]
    [MemberData(nameof(Pages.AdminData), MemberType = typeof(Pages))]
    public Task Admin_pages(string path) => AssertStructureAsync(fixture.Configured, Role.Admin, path);

    [Fact]
    public Task Setup_wizard() => AssertStructureAsync(fixture.Fresh, Role.Anonymous, "/setup");

    [Fact]
    public async Task Library_posters_are_decoded_by_the_browser()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var broken = await page.EvaluateAsync<int>("() => [...document.querySelectorAll('.library-card img')].filter(i => !i.complete || i.naturalWidth === 0).length");
        var total = await page.EvaluateAsync<int>("() => document.querySelectorAll('.library-card img').length");

        Assert.True(total > 0, "Es sind keine Poster zu sehen.");
        Assert.Equal(0, broken);
    }

    [Fact]
    public async Task Browser_storage_stays_empty_and_only_the_two_documented_cookies_exist()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Admin);
        var page = await context.NewPageAsync();
        foreach (var path in Pages.SignedIn.Concat(Pages.AdminOnly))
        {
            await page.GotoAsync(path);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        var storage = await page.EvaluateAsync<int[]>("() => [localStorage.length, sessionStorage.length]");
        var databases = await page.EvaluateAsync<int>("async () => (await indexedDB.databases()).length");
        var cookieNames = (await context.CookiesAsync()).Select(c => c.Name).ToList();

        Assert.Equal([0, 0], storage);
        Assert.Equal(0, databases);
        Assert.All(cookieNames, name => Assert.True(
            AuthCookie.IsAuthCookie(name) || name is "Wunschkino.TwoFactor" || name.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal), $"Unerwartetes Cookie: {name}"));
    }

    private async Task AssertStructureAsync(AppHost host, Role role, string path)
    {
        await using var context = await fixture.NewContextAsync(host, role);
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Equal(1, await page.Locator("h1").CountAsync());
        Assert.Equal(1, await page.Locator("main").CountAsync());
        Assert.Equal(1, await page.Locator("footer").CountAsync());
        Assert.Equal("de", await page.EvaluateAsync<string>("document.documentElement.lang"));
        Assert.False(string.IsNullOrWhiteSpace(await page.TitleAsync()));
    }

    [Fact]
    public async Task Password_fields_carry_autocomplete_hints_for_password_managers()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.User);
        var page = await context.NewPageAsync();
        await page.GotoAsync("/account/password");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var hints = await page.EvaluateAsync<string[]>("() => [...document.querySelectorAll('input[type=password]')].map(i => i.autocomplete)");

        Assert.Equal(["current-password", "new-password", "new-password"], hints);
    }
}
