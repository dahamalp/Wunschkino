using Microsoft.Playwright;

namespace Wunschkino.E2E;

/// <summary>
/// Rechtliche Leitplanke: Der Browser darf auf keiner Seite einen Server außerhalb der eigenen
/// Herkunft kontaktieren (Schriften, Skripte, Bilder, WebSockets, Favicons).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class NoThirdPartyRequestsTests(BrowserFixture fixture)
{
    [Theory]
    [MemberData(nameof(Pages.AnonymousData), MemberType = typeof(Pages))]
    public Task Anonymous_pages(string path) => AssertOnlyOwnOriginAsync(fixture.Configured, Role.Anonymous, path);

    [Theory]
    [MemberData(nameof(Pages.SignedInData), MemberType = typeof(Pages))]
    public Task Signed_in_pages(string path) => AssertOnlyOwnOriginAsync(fixture.Configured, Role.User, path);

    [Theory]
    [MemberData(nameof(Pages.AdminData), MemberType = typeof(Pages))]
    public Task Admin_pages(string path) => AssertOnlyOwnOriginAsync(fixture.Configured, Role.Admin, path);

    [Fact]
    public Task Setup_wizard() => AssertOnlyOwnOriginAsync(fixture.Fresh, Role.Anonymous, "/setup");

    /// <summary>Gegenprobe: Der Wächter schlägt an, wenn die Seite eine fremde Herkunft anfragt (die Anfrage selbst wird abgefangen).</summary>
    [Fact]
    public async Task Watcher_detects_foreign_requests()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        await context.RouteAsync("https://fonts.example.test/**", route => route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = "" }));
        var page = await context.NewPageAsync();
        var foreign = Watch(page, fixture.Configured);

        await page.GotoAsync("about:blank");
        await page.SetContentAsync("<link rel=stylesheet href=\"https://fonts.example.test/css\">");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Contains(foreign, url => url.StartsWith("https://fonts.example.test/"));
    }

    private async Task AssertOnlyOwnOriginAsync(AppHost host, Role role, string path)
    {
        await using var context = await fixture.NewContextAsync(host, role);
        var page = await context.NewPageAsync();
        var foreign = Watch(page, host);

        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Empty(foreign);
    }

    private static List<string> Watch(IPage page, AppHost host)
    {
        var foreign = new List<string>();
        var origin = new Uri(host.BaseAddress);

        void Check(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is "data" or "blob" or "about")
                return;
            var sameOrigin = uri.Host == origin.Host && uri.Port == origin.Port && uri.Scheme is "http" or "https" or "ws" or "wss";
            if (!sameOrigin)
                foreign.Add(url);
        }

        page.Request += (_, request) => Check(request.Url);
        page.WebSocket += (_, socket) => Check(socket.Url);
        return foreign;
    }
}
