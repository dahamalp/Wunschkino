using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace Wunschkino.E2E;

/// <summary>
/// Barrierefreiheitsprüfung mit axe-core (WCAG 2.1 AA) auf jeder Seite, hell und dunkel.
/// Der Lauf schreibt alle Funde in den Bericht <c>axe-report.txt</c> und lässt den Test scheitern, sobald
/// ein Verstoß der Stufe „serious“ oder höher vorliegt (<see cref="Strict"/> = false sammelt nur).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class AxeTests(BrowserFixture fixture)
{
    private static readonly bool Strict = true;
    private static readonly string[] BlockingImpacts = ["serious", "critical"];
    private static readonly string[] Tags = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"];
    private static readonly string AxeSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "axe.min.js"));
    private static readonly string ReportPath = Path.Combine(AppContext.BaseDirectory, "axe-report.txt");
    private static readonly Lock ReportLock = new();

    [Theory]
    [MemberData(nameof(Pages.AnonymousData), MemberType = typeof(Pages))]
    public Task Anonymous_pages(string path) => CheckAsync(fixture.Configured, Role.Anonymous, path);

    [Theory]
    [MemberData(nameof(Pages.SignedInData), MemberType = typeof(Pages))]
    public Task Signed_in_pages(string path) => CheckAsync(fixture.Configured, Role.User, path);

    [Theory]
    [MemberData(nameof(Pages.AdminData), MemberType = typeof(Pages))]
    public Task Admin_pages(string path) => CheckAsync(fixture.Configured, Role.Admin, path);

    [Fact]
    public Task Setup_wizard() => CheckAsync(fixture.Fresh, Role.Anonymous, "/setup");

    // Jede Sprache auf je einer Seite pro Rolle: Übersetzungen ändern Längen, Beschriftungen und das lang-Attribut.
    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    [InlineData("es-ES")]
    [InlineData("nl-NL")]
    public async Task Every_language(string locale)
    {
        await CheckAsync(fixture.Configured, Role.Anonymous, "/login", locale);
        await CheckAsync(fixture.Configured, Role.User, "/wishes", locale);
        await CheckAsync(fixture.Configured, Role.Admin, "/admin/settings", locale);
    }

    private async Task CheckAsync(AppHost host, Role role, string path, string? locale = null)
    {
        var blocking = new List<string>();
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        {
            await using var context = await fixture.NewContextAsync(host, role, scheme: scheme, locale: locale);
            var page = await context.NewPageAsync();
            await page.GotoAsync(path);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            await page.EvaluateAsync(AxeSource);
            var json = await page.EvaluateAsync<JsonElement>(
                "tags => axe.run(document, { runOnly: { type: 'tag', values: tags } }).then(r => r.violations.map(v => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.length, target: v.nodes.slice(0, 3).map(n => n.target.join(' ')) })))",
                Tags);

            foreach (var violation in json.EnumerateArray())
            {
                var impact = violation.GetProperty("impact").GetString() ?? "";
                var line = $"[{scheme}{(locale is null ? "" : " " + locale)}] {role} {path}: {violation.GetProperty("id").GetString()} ({impact}, {violation.GetProperty("nodes").GetInt32()}x) {violation.GetProperty("help").GetString()} | {string.Join(" ; ", violation.GetProperty("target").EnumerateArray().Select(t => t.GetString()))}";
                lock (ReportLock)
                    File.AppendAllText(ReportPath, line + Environment.NewLine, Encoding.UTF8);
                if (BlockingImpacts.Contains(impact))
                    blocking.Add(line);
            }
        }

        if (Strict)
            Assert.Empty(blocking);
    }

    /// <summary>Gegenprobe: axe meldet einen bekannten Verstoß (Eingabefeld ohne Beschriftung), sonst wäre ein grüner Lauf wertlos.</summary>
    [Fact]
    public async Task Axe_detects_a_known_violation()
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, Role.Anonymous);
        var page = await context.NewPageAsync();
        await page.GotoAsync("about:blank");
        await page.SetContentAsync("<main><h1>Test</h1><input type=\"text\"></main>");

        await page.EvaluateAsync(AxeSource);
        var ids = await page.EvaluateAsync<string[]>("tags => axe.run(document, { runOnly: { type: 'tag', values: tags } }).then(r => r.violations.map(v => v.id))", Tags);

        Assert.Contains("label", ids);
    }
}
