using Microsoft.Playwright;

namespace Wunschkino.E2E;

/// <summary>
/// Touch-Ziele auf dem Handy: Jedes bedienbare Element ist mindestens 44 × 44 CSS-Pixel groß.
/// Ausgenommen sind Links im Fließtext (WCAG 2.5.5 „inline“) und Elemente, die gerade nicht sichtbar sind.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class TouchTargetTests(BrowserFixture fixture)
{
    private const int MinSize = 44;
    private static readonly ViewportSize Phone = new() { Width = 390, Height = 844 };

    // Liefert alle zu kleinen Ziele als „Beschreibung (B×H)“. Gemessen wird die Trefferfläche: Ein kleines Symbol in einem
    // großen Knopf zählt als groß.
    private const string FindSmallTargets = """
        (min) => {
          const selector = 'a[href], button, input:not([type=hidden]), select, textarea, [role=button], [role=menuitem], [role=checkbox], [role=switch], [tabindex]:not([tabindex="-1"])';
          const small = [];
          for (const el of document.querySelectorAll(selector)) {
            const style = getComputedStyle(el);
            if (style.visibility === 'hidden' || style.display === 'none') continue;
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            if (el.closest('.mud-drawer--closed, .visually-hidden, .skip-link')) continue;
            // Inline-Links im Fließtext sind ausgenommen.
            if (el.tagName === 'A' && style.display === 'inline' && el.closest('p, li, .mud-typography-body1, .mud-typography-body2')) continue;
            // Checkbox und Radio: Die Trefferfläche ist das umgebende Label bzw. der MudBlazor-Knopf.
            if (el.tagName === 'INPUT' && (el.type === 'checkbox' || el.type === 'radio')) {
              const box = (el.closest('label, .mud-button-root') || el).getBoundingClientRect();
              if (box.width >= min && box.height >= min) continue;
            }
            // Ein Feld in einem höheren MudBlazor-Eingaberahmen gilt mit dem Rahmen.
            if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') {
              const box = (el.closest('.mud-input-control, .mud-input') || el).getBoundingClientRect();
              if (box.height >= min) continue;
            }
            if (r.width >= min - 0.5 && r.height >= min - 0.5) continue;
            const label = (el.getAttribute('aria-label') || el.textContent || el.getAttribute('name') || el.id || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 40);
            small.push(`${el.tagName.toLowerCase()} "${label}" (${Math.round(r.width)}×${Math.round(r.height)})`);
          }
          return small;
        }
        """;

    [Theory]
    [MemberData(nameof(Pages.AnonymousData), MemberType = typeof(Pages))]
    public Task Anonymous_pages(string path) => CheckAsync(Role.Anonymous, path);

    [Theory]
    [MemberData(nameof(Pages.SignedInData), MemberType = typeof(Pages))]
    public Task Signed_in_pages(string path) => CheckAsync(Role.User, path);

    [Theory]
    [MemberData(nameof(Pages.AdminData), MemberType = typeof(Pages))]
    public Task Admin_pages(string path) => CheckAsync(Role.Admin, path);

    private async Task CheckAsync(Role role, string path)
    {
        await using var context = await fixture.NewContextAsync(fixture.Configured, role, Phone);
        var page = await context.NewPageAsync();
        await page.GotoAsync(path);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var small = await page.EvaluateAsync<string[]>(FindSmallTargets, MinSize);

        Assert.True(small.Length == 0, $"{path}: zu kleine Touch-Ziele:\n  " + string.Join("\n  ", small));
    }
}
