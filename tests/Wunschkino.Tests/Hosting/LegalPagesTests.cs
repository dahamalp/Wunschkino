using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Data;
using Wunschkino.Legal;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Hosting;

public class LegalPagesTests
{
    [Theory]
    [InlineData("/datenschutz")]
    [InlineData("/impressum")]
    public async Task LegalPages_AreReachableWithoutSignIn_AndEvenBeforeTheSetup(string path)
    {
        await using var factory = new WunschkinoFactory();

        var response = await factory.CreateNonRedirectingClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Privacy_ShowsTheBuiltInText_ListsTheCookies_AndNamesNoContactWhenNoneIsSet()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();

        var html = await factory.CreateNonRedirectingClient().GetStringAsync("/datenschutz");

        Assert.Contains("Wunschkino.Auth", html);
        Assert.Contains("Antiforgery", html);
        Assert.Contains("TMDB", html);
        Assert.Contains("keine Kontaktangabe", html);
    }

    [Fact]
    public async Task Privacy_ShowsTheOperatorsTextInsteadOfTheBuiltInOne_AndTheContact()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        await SaveAsync(factory, new LegalTexts(null, "Eigene Erklaerung des Betreibers", "Familie Muster, Musterweg 1"));

        var html = await factory.CreateNonRedirectingClient().GetStringAsync("/datenschutz");

        Assert.Contains("Eigene Erklaerung des Betreibers", html);
        Assert.Contains("Familie Muster, Musterweg 1", html);
        Assert.DoesNotContain("Antiforgery", html);
    }

    [Fact]
    public async Task OperatorText_IsEncoded_NeverRenderedAsHtml()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        await SaveAsync(factory, new LegalTexts("<script>alert(1)</script><img src=\"https://example.test/x.png\">", null, null));

        var html = await factory.CreateNonRedirectingClient().GetStringAsync("/impressum");

        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("<img src=\"https://example.test", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task Footer_LinksToThePrivacyPage_AndToTheImprintOnlyWhenOneExists()
    {
        await using var factory = new WunschkinoFactory();
        await factory.SeedAdminAsync();
        var client = factory.CreateNonRedirectingClient();

        var before = await client.GetStringAsync("/login");
        await SaveAsync(factory, new LegalTexts("Impressum des Betreibers", null, null));
        var after = await client.GetStringAsync("/login");

        Assert.Contains("href=\"/datenschutz\"", before);
        Assert.DoesNotContain("href=\"/impressum\"", before);
        Assert.Contains("href=\"/impressum\"", after);
    }

    private static async Task SaveAsync(WunschkinoFactory factory, LegalTexts texts)
    {
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var adminId = await db.Users.Where(u => u.Role == UserRole.Admin).Select(u => u.Id).FirstAsync();

        var result = await factory.Services.GetRequiredService<LegalService>().SaveAsync(adminId, texts);

        Assert.True(result.Success);
    }
}
