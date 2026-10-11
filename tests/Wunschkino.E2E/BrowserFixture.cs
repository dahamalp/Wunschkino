using Microsoft.Playwright;

namespace Wunschkino.E2E;

public enum Role { Anonymous, User, Admin }

/// <summary>Ein Browser und zwei App-Instanzen (eingerichtet, frisch) für alle E2E-Tests.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private readonly Dictionary<Role, string> _signedInStates = [];
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private FakeJellyfin _jellyfin = null!;

    // Die Tests prüfen die deutschen Texte; ohne feste Sprache nähme die App die des Rechners, auf dem der Browser läuft.
    public const string Locale = "de-DE";

    public AppHost Configured { get; } = new(seed: true);
    public AppHost Fresh { get; } = new(seed: false);

    public async Task InitializeAsync()
    {
        await Configured.StartAsync();
        _jellyfin = await FakeJellyfin.StartAsync();
        await Configured.SeedDemoAsync(_jellyfin);
        await Fresh.StartAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        Configured.Dispose();
        Fresh.Dispose();
        await _jellyfin.DisposeAsync();
    }

    public async Task<IBrowserContext> NewContextAsync(AppHost host, Role role, ViewportSize? viewport = null, ColorScheme scheme = ColorScheme.Light, string? locale = null)
    {
        return await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.BaseAddress,
            Locale = locale ?? Locale,
            ViewportSize = viewport ?? new ViewportSize { Width = 1280, Height = 800 },
            ColorScheme = scheme,
            StorageState = role == Role.Anonymous ? null : await SignedInStateAsync(host, role)
        });
    }

    // Einmal je Rolle anmelden: Die Bremse je Benutzername lässt schnelle wiederholte Anmeldungen sonst hängen.
    private async Task<string> SignedInStateAsync(AppHost host, Role role)
    {
        await _loginLock.WaitAsync();
        try
        {
            if (_signedInStates.TryGetValue(role, out var cached))
                return cached;

            await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = host.BaseAddress, Locale = Locale });
            var page = await context.NewPageAsync();
            await page.GotoAsync("/login");
            await page.FillAsync("#username", role == Role.Admin ? AppHost.AdminName : AppHost.UserName);
            await page.FillAsync("#password", AppHost.Password);
            await page.ClickAsync("button[type=submit]");
            await page.WaitForURLAsync(url => !url.Contains("/login"));
            return _signedInStates[role] = await context.StorageStateAsync();
        }
        finally
        {
            _loginLock.Release();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>
{
    public const string Name = "Browser";
}
