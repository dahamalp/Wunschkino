using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using Wunschkino.Components.Library;
using Wunschkino.Data;
using Wunschkino.Gaps;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Components;

public class LibraryDetailsDialogTests : ComponentTestBase
{
    private static readonly LibraryItem Movie = JellyfinFixtures.LibraryItem("m1", "Beispielfilm", MediaKind.Movie, 2020);
    private static readonly LibraryItem Series = JellyfinFixtures.LibraryItem("s1", "Beispielserie", MediaKind.Series, 2015);

    private readonly TestDb _db;
    private Func<HttpRequestMessage, HttpResponseMessage> _jellyfin = Respond;

    public LibraryDetailsDialogTests()
    {
        _db = Own(new TestDb());
        var library = new LibraryCache();
        library.Update([Movie, Series], DateTime.UtcNow);
        var settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        settings.SetAsync(SettingKeys.JellyfinUrl, "http://jellyfin:8096", isProtected: false).GetAwaiter().GetResult();
        settings.SetAsync(SettingKeys.JellyfinApiKey, "key", isProtected: true).GetAwaiter().GetResult();

        Services.AddSingleton(library);
        Services.AddSingleton(settings);
        Services.AddSingleton<IDbContextFactory<AppDbContext>>(_db);
        Services.AddSingleton(new JellyfinClient(new HttpClient(new FakeHttpHandler(request => _jellyfin(request)))));
        Services.AddMemoryCache();
        Services.AddLogging();
        Services.AddScoped<LibraryDetailsService>();
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath.EndsWith("/Episodes", StringComparison.Ordinal))
            return Json("""{"Items":[{"Name":"Pilot","ParentIndexNumber":1,"IndexNumber":1},{"Name":"Finale","ParentIndexNumber":1,"IndexNumber":3}]}""");
        return uri.Query.Contains("Ids=m1&")
            ? Json("""
                {"Items":[{"Id":"m1","Name":"Beispielfilm","Overview":"Erster Absatz<br>Zweiter <b>Absatz</b>","Genres":["Drama"],
                  "OfficialRating":"FSK-12","CommunityRating":7.8,"RunTimeTicks":67200000000,
                  "People":[{"Name":"Regina Regie","Type":"Director"},{"Name":"Anna Beispiel","Role":"Hauptrolle","Type":"Actor"}]}]}
                """)
            : Json("""{"Items":[{"Id":"s1","Name":"Beispielserie","Status":"Ended","EndDate":"2019-06-01T00:00:00Z"}]}""");
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(LibraryItem item)
    {
        var dialogs = Dialogs;
        var service = Services.GetRequiredService<IDialogService>();
        await dialogs.InvokeAsync(() => service.ShowAsync<LibraryDetailsDialog>(item.Title,
            new DialogParameters<LibraryDetailsDialog> { { d => d.Item, item } }));
        return dialogs;
    }

    [Fact]
    public async Task AMovie_ShowsItsMetadata_AsPlainText()
    {
        var dialogs = await OpenAsync(Movie);

        dialogs.WaitForAssertion(() => Assert.Contains("Regina Regie", dialogs.Markup), Patience);
        var text = dialogs.Find(".library-details").TextContent;
        Assert.Contains("Beispielfilm", text);
        Assert.Contains("FSK-12", text);
        Assert.Contains("112 Min.", text);
        Assert.Contains("7,8", text);
        Assert.Contains("Drama", text);
        Assert.Contains("Hauptrolle", text);
        Assert.Equal("Erster Absatz\nZweiter Absatz", dialogs.Find(".library-details-overview").TextContent.Trim());
        Assert.DoesNotContain("<b>", dialogs.Markup);
        Assert.Empty(dialogs.FindAll(".library-details-seasons"));
    }

    [Fact]
    public async Task ASeries_ListsItsEpisodes_AndMarksTheMissingOnes()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.SeriesGaps.Add(new SeriesGap
            {
                JellyfinId = "s1", Title = "Beispielserie", HasResult = true, Status = GapStatus.Missing,
                MissingJson = SeriesGapSerializer.Serialize([new SeasonGap(1, [2], false)])
            });
            await db.SaveChangesAsync();
        }

        var dialogs = await OpenAsync(Series);

        dialogs.WaitForAssertion(() => Assert.Contains("Staffel 1 · 2 Folgen · 1 fehlt", dialogs.Markup), Patience);
        Assert.Contains("2015–2019", dialogs.Markup);
        var rows = dialogs.FindAll(".library-details-episodes li").Select(li => li.TextContent.Trim()).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Contains("Pilot", rows[0]);
        Assert.Contains("fehlt", rows[1]);
        Assert.Contains("Finale", rows[2]);
        Assert.Single(dialogs.FindAll(".library-details-missing"));
    }

    [Fact]
    public async Task AJellyfinFailure_ShowsAMessage_InsteadOfDetails()
    {
        _jellyfin = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var dialogs = await OpenAsync(Movie);

        dialogs.WaitForAssertion(() => Assert.Contains("HTTP 500", dialogs.Find(".mud-alert").TextContent), Patience);
        Assert.Contains("Beispielfilm", dialogs.Markup);
    }
}
