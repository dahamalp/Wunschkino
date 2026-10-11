using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Anime;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using Wunschkino.Wishes;
using SettingsPage = Wunschkino.Components.Pages.Admin.Settings;

namespace Wunschkino.Tests.Components;

public class SettingsPageTests : ComponentTestBase
{
    private readonly AnimeIdMapping _mapping = new();
    private readonly AnimeIdMappingUpdater _updater;
    private readonly SettingsService _settings;
    private readonly AuditLog _audit;
    private bool _listDownloadFails;

    public SettingsPageTests()
    {
        var db = Own(new TestDb());
        SignInAsAdmin(TestUsers.Add(db, "admin", UserRole.Admin).Id);
        Services.AddSingleton(new AdminGuard(db));
        var directory = Own(new TempDirectory());

        var settings = new SettingsService(db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _settings = settings;
        Services.AddSingleton(settings);
        _audit = new AuditLog(db, TimeProvider.System, NullLogger<AuditLog>.Instance);
        Services.AddSingleton(_audit);
        Services.AddSingleton(new Wunschkino.Localization.LanguagePreferenceService(db, settings));
        Services.AddSingleton(new Wunschkino.Legal.LegalService(settings, db, _audit));
        Services.AddSingleton(new ConnectionSettingsService(
            db,
            settings,
            new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK))),
            new TmdbClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK))),
            new UserService(db, new PlainTextHasher(), TimeProvider.System)));

        // Der Hintergrunddienst läuft im Test nicht von selbst; die Tests stoßen einen Lauf per RefreshOnceAsync an.
        var listServices = new ServiceCollection();
        listServices.AddScoped(_ => new AnimeListClient(new HttpClient(new FakeHttpHandler(_ => _listDownloadFails
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : ListResponse(150)))));
        var listScopes = listServices.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _updater = new AnimeIdMappingUpdater(listScopes, _mapping, new AnimeListStore(directory.Path), TimeProvider.System, NullLogger<AnimeIdMappingUpdater>.Instance);

        Services.AddSingleton(_mapping);
        Services.AddSingleton(_updater);
        Services.AddSingleton(new LibraryRefreshService(
            listScopes,
            new LibraryCache(),
            new WishAutoCompleter(db, _mapping, TimeProvider.System, NullLogger<WishAutoCompleter>.Instance),
            TimeProvider.System,
            NullLogger<LibraryRefreshService>.Instance));
    }

    private static HttpResponseMessage ListResponse(int count)
    {
        var json = "[" + string.Join(',', Enumerable.Range(1, count).Select(i => "{\"type\":\"TV\",\"anilist_id\":" + i + ",\"themoviedb_id\":{\"tv\":" + (i + 1000) + "}}")) + "]";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        return response;
    }

    [Fact]
    public void WishVisibility_IsAnonymousByDefault_AndTheSwitchSavesImmediately()
    {
        var page = RenderLoadedPage();
        var toggle = page.Find("input[type=checkbox]");
        Assert.Contains("Wünsche anonym anzeigen", page.Markup);
        Assert.Contains("Admins sehen es immer", page.Markup);
        Assert.True(WishPrivacy.IsAnonymous(_settings.GetAsync(SettingKeys.WishesAnonymous).GetAwaiter().GetResult()));

        toggle.Change(false);

        page.WaitForAssertion(() =>
            Assert.Equal("false", _settings.GetAsync(SettingKeys.WishesAnonymous).GetAwaiter().GetResult()), Patience);

        page.Find("input[type=checkbox]").Change(true);

        page.WaitForAssertion(() =>
            Assert.Equal("true", _settings.GetAsync(SettingKeys.WishesAnonymous).GetAwaiter().GetResult()), Patience);
    }

    private static IRenderedComponent<MudBlazor.MudNumericField<int>> NumericField(IRenderedComponent<SettingsPage> page, string labelStart) =>
        page.FindComponents<MudBlazor.MudNumericField<int>>().Single(f => f.Instance.Label!.StartsWith(labelStart, StringComparison.Ordinal));

    private string? StoredSetting(string key) => _settings.GetAsync(key).GetAwaiter().GetResult();

    [Fact]
    public void UserLimits_ShowTheDefaults_AndSavingStoresTheChosenValues()
    {
        var page = RenderLoadedPage();
        Assert.Equal("20", NumericField(page, "Wünsche pro Nutzer und Tag").Find("input").GetAttribute("value"));
        Assert.Equal("10", NumericField(page, "TMDB-Suchen").Find("input").GetAttribute("value"));

        NumericField(page, "Wünsche pro Nutzer und Tag").Find("input").Change("5");
        NumericField(page, "TMDB-Suchen").Find("input").Change("0");
        page.FindAll("button").Single(b => b.TextContent.Contains("Begrenzungen speichern")).Click();

        page.WaitForAssertion(() =>
        {
            Assert.Equal("5", StoredSetting(SettingKeys.WishesPerDay));
            Assert.Equal("0", StoredSetting(SettingKeys.TmdbSearchesPerMinute));
        }, Patience);
        Assert.Contains("0 heißt: kein Limit", page.Markup);
        Assert.Contains("Admins sind ausgenommen", page.Markup);
    }

    [Fact]
    public async Task ChangingTheWishSettings_IsWrittenToTheAuditLog_WithTheAdminAsActor()
    {
        var page = RenderLoadedPage();

        page.Find("input[type=checkbox]").Change(false);
        page.WaitForAssertion(() => Assert.Contains(_audit.ListAsync().GetAwaiter().GetResult(), e => e.Detail == "Wünsche werden mit Namen angezeigt"), Patience);
        NumericField(page, "Wünsche pro Nutzer und Tag").Find("input").Change("5");
        page.FindAll("button").Single(b => b.TextContent.Contains("Begrenzungen speichern")).Click();

        page.WaitForAssertion(() => Assert.Contains(_audit.ListAsync().GetAwaiter().GetResult(), e => e.Detail!.StartsWith("Wünsche pro Tag: 5", StringComparison.Ordinal)), Patience);
        var entries = await _audit.ListAsync();
        Assert.All(entries, e =>
        {
            Assert.Equal(AuditActions.WishSettingsChanged, e.Action);
            Assert.Equal("admin", e.ActorName);
        });
    }

    [Fact]
    public void UserLimits_ShowTheSavedValues_WhenThePageIsOpenedAgain()
    {
        _settings.SetAsync(SettingKeys.WishesPerDay, "7", isProtected: false).GetAwaiter().GetResult();

        var page = RenderLoadedPage();

        Assert.Equal("7", NumericField(page, "Wünsche pro Nutzer und Tag").Find("input").GetAttribute("value"));
    }

    [Fact]
    public void UserLimits_ValuesBeyondTheMaximum_AreSavedAsTheMaximum()
    {
        var page = RenderLoadedPage();

        NumericField(page, "Wünsche pro Nutzer und Tag").Find("input").Change("999999");
        page.FindAll("button").Single(b => b.TextContent.Contains("Begrenzungen speichern")).Click();

        page.WaitForAssertion(() => Assert.Equal(UserLimits.MaxWishesPerDay.ToString(), StoredSetting(SettingKeys.WishesPerDay)), Patience);
    }

    private IRenderedComponent<SettingsPage> RenderLoadedPage()
    {
        var page = RenderPage<SettingsPage>();
        page.WaitForAssertion(() => Assert.Contains("Anime-Zuordnungsliste", page.Markup), Patience);
        return page;
    }

    private static IElement AnimeButton(IRenderedComponent<SettingsPage> page) =>
        page.FindAll("button").Single(b => b.TextContent.Contains("Anime-Liste jetzt aktualisieren") || b.TextContent.Contains("Wird aktualisiert"));

    private static void ClickAnimeButton(IRenderedComponent<SettingsPage> page) => AnimeButton(page).Click();

    [Fact]
    public void AnimeList_NotLoadedYet_SaysSo()
    {
        var page = RenderLoadedPage();

        Assert.Contains("noch nicht geladen", page.Markup);
    }

    [Fact]
    public void AnimeList_Loaded_ShowsCountAndCheckTime()
    {
        _mapping.Replace(new Dictionary<int, AnimeMapping>
        {
            [1] = new(1, MediaKind.Series, 1001, []),
            [2] = new(2, MediaKind.Series, 1002, []),
            [3] = new(3, MediaKind.Movie, 1003, [])
        }, DateTime.UtcNow);

        var page = RenderLoadedPage();

        Assert.Contains("3 Einträge", page.Markup);
        Assert.Contains("zuletzt geprüft", page.Markup);
        Assert.DoesNotContain("noch nicht geladen", page.Markup);
    }

    [Fact]
    public async Task Refresh_AfterASuccessfulRun_ReportsSuccess_AndDisablesTheButtonMeanwhile()
    {
        var page = RenderLoadedPage();

        ClickAnimeButton(page);
        page.WaitForAssertion(() => Assert.True(AnimeButton(page).HasAttribute("disabled")), Patience);
        await _updater.RefreshOnceAsync(CancellationToken.None);

        Snackbars.WaitForAssertion(() => Assert.Contains("Anime-Liste aktualisiert.", Snackbars.Markup), Patience);
        page.WaitForAssertion(() => Assert.False(AnimeButton(page).HasAttribute("disabled")), Patience);
    }

    [Fact]
    public async Task Refresh_AfterAFailedRun_ShowsTheWarning_NotSuccess()
    {
        _listDownloadFails = true;
        var page = RenderLoadedPage();

        ClickAnimeButton(page);
        await _updater.RefreshOnceAsync(CancellationToken.None);

        Snackbars.WaitForAssertion(() => Assert.Contains("konnte nicht geladen werden", Snackbars.Markup), Patience);
        Assert.DoesNotContain("Anime-Liste aktualisiert.", Snackbars.Markup);
    }

    [Fact]
    public async Task Refresh_WhenThePageIsLeftWhileWaiting_ReportsNothingAnymore()
    {
        var page = RenderLoadedPage();
        ClickAnimeButton(page);
        page.WaitForAssertion(() => Assert.True(AnimeButton(page).HasAttribute("disabled")), Patience);

        // bUnit entsorgt die Seite selbst; ein zusätzliches manuelles Dispose würde die CancellationTokenSource doppelt freigeben.
        await DisposeComponentsAsync();
        await _updater.RefreshOnceAsync(CancellationToken.None);
        // Mehr als ein Poll-Takt der Seite (500 ms): Hätte sie weitergewartet, hätte sie inzwischen den abgeschlossenen Lauf gemeldet.
        await Task.Delay(800);

        Assert.Empty(Services.GetRequiredService<MudBlazor.ISnackbar>().ShownSnackbars);
    }
}
