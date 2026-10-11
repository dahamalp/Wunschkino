using System.Globalization;
using System.Resources;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Localization;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Localization;

public partial class LocalizationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly LanguagePreferenceService _languages;

    public LocalizationTests() =>
        _languages = new LanguagePreferenceService(_db, new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance));

    public void Dispose() => _db.Dispose();

    private static string ResourcesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Wunschkino.slnx")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "Wunschkino", "Resources");
    }

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    private static string[] Placeholders(string text) =>
        [.. Placeholder().Matches(text).Select(m => m.Value).Distinct().Order()];

    public static IEnumerable<object[]> Translations() =>
        AppLanguages.All.Where(l => l.Code != AppLanguages.Default).Select(l => new object[] { l.Code });

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryLanguageFile_HasExactlyTheKeysOfTheGermanFile(string code)
    {
        var german = ReadResx(Path.Combine(ResourcesDirectory(), "Texts.resx"));
        var translated = ReadResx(Path.Combine(ResourcesDirectory(), $"Texts.{code}.resx"));

        Assert.Empty(german.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(german.Keys));
        Assert.All(translated, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), $"{code}: {entry.Key} ist leer"));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryTranslation_KeepsThePlaceholders(string code)
    {
        var german = ReadResx(Path.Combine(ResourcesDirectory(), "Texts.resx"));
        var translated = ReadResx(Path.Combine(ResourcesDirectory(), $"Texts.{code}.resx"));

        var broken = german.Where(e => translated.TryGetValue(e.Key, out var t) && !Placeholders(e.Value).SequenceEqual(Placeholders(t)))
            .Select(e => e.Key).ToList();

        Assert.True(broken.Count == 0, $"{code}: Platzhalter weichen ab bei {string.Join(", ", broken)}");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void TheAppLoadsTheTranslation_FromItsSatelliteAssembly(string code)
    {
        var resources = new ResourceManager("Wunschkino.Resources.Texts", typeof(AppLanguages).Assembly);

        Assert.NotEqual(resources.GetString("Login_Submit", CultureInfo.GetCultureInfo("de")), resources.GetString("Login_Submit", CultureInfo.GetCultureInfo(code)));
    }

    [Theory]
    [InlineData("en-GB", "en")]
    [InlineData("FR", "fr")]
    [InlineData("nl_BE", "nl")]
    [InlineData("it", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_AcceptsOnlySupportedLanguages(string? input, string? expected) =>
        Assert.Equal(expected, AppLanguages.Normalize(input));

    [Theory]
    [InlineData("de", "de-DE")]
    [InlineData("en", "en-US")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("it", "de-DE")]
    public void TmdbCode_FollowsTheInterfaceLanguage(string culture, string expected) =>
        Assert.Equal(expected, AppLanguages.TmdbCode(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public async Task AccountLanguage_IsStored_AndAutomaticClearsIt()
    {
        var anna = TestUsers.Add(_db, "anna");

        await _languages.SetAsync(anna.Id, "fr");
        Assert.Equal("fr", await _languages.GetAsync(anna.Id));
        Assert.Equal("fr", TestUsers.Reload(_db, anna.Id).Language);

        await _languages.SetAsync(anna.Id, null);
        Assert.Null(await _languages.GetAsync(anna.Id));
    }

    [Fact]
    public async Task UnknownLanguages_AreStoredAsAutomatic()
    {
        var anna = TestUsers.Add(_db, "anna");

        await _languages.SetAsync(anna.Id, "klingonisch");

        Assert.Null(TestUsers.Reload(_db, anna.Id).Language);
    }

    [Fact]
    public async Task DefaultLanguage_IsGermanUntilTheAdminChoosesAnother()
    {
        Assert.Equal("de", await _languages.GetDefaultAsync());

        await _languages.SetDefaultAsync("es");

        Assert.Equal("es", await _languages.GetDefaultAsync());
    }

    private HttpContext Context(ClaimsPrincipal? user = null) => new DefaultHttpContext
    {
        User = user ?? new ClaimsPrincipal(new ClaimsIdentity()),
        RequestServices = new ServiceCollection().AddSingleton(_languages).BuildServiceProvider()
    };

    [Fact]
    public async Task AccountProvider_UsesTheAccountLanguage_AndStaysOutForAnonymousVisitors()
    {
        var anna = TestUsers.Add(_db, "anna");
        await _languages.SetAsync(anna.Id, "nl");
        var provider = new AccountLanguageProvider();

        var signedIn = await provider.DetermineProviderCultureResult(Context(AuthClaims.CreatePrincipal(TestUsers.Reload(_db, anna.Id))));
        var anonymous = await provider.DetermineProviderCultureResult(Context());

        Assert.Equal("nl", signedIn!.UICultures.Single().Value);
        Assert.Null(anonymous);
    }

    [Fact]
    public async Task DefaultProvider_ReturnsTheAdminsChoice()
    {
        await _languages.SetDefaultAsync("en");

        var result = await new DefaultLanguageProvider().DetermineProviderCultureResult(Context());

        Assert.Equal("en", result!.UICultures.Single().Value);
    }

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
