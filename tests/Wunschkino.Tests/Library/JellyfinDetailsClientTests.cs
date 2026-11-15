using System.Net;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Library;

public class JellyfinDetailsClientTests
{
    private const string FullItem = """
        {"Items":[{
          "Id":"s1","Name":"Beispielserie","Type":"Series",
          "Overview":"Erste Zeile<br>Zweite &amp; letzte <b>Zeile</b>",
          "Taglines":["Alles beginnt hier"],
          "Genres":["Drama","Krimi"],
          "OfficialRating":"FSK-16","CommunityRating":8.25,"RunTimeTicks":27000000000,
          "Status":"Ended","EndDate":"2019-05-19T00:00:00.0000000Z",
          "Studios":[{"Name":"Studio Nord"},{"Name":""}],
          "People":[
            {"Name":"Erika Regie","Type":"Director"},
            {"Name":"Max Muster","Role":"Kommissar","Type":"Actor"},
            {"Name":"Gast Star","Role":"Zeuge","Type":"GuestStar"},
            {"Name":"Ohne Rolle","Type":"Actor"},
            {"Name":"","Role":"Niemand","Type":"Actor"}
          ]
        }]}
        """;

    [Fact]
    public async Task GetDetails_MapsTheMetadata_AndTurnsTheOverviewIntoPlainText()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, FullItem);
        var client = new JellyfinClient(new HttpClient(handler));

        var details = await client.GetDetailsAsync("http://jellyfin:8096", "key", "s1");

        Assert.NotNull(details);
        Assert.Equal("Erste Zeile\nZweite & letzte Zeile", details.Overview);
        Assert.Equal("Alles beginnt hier", details.Tagline);
        Assert.Equal(["Drama", "Krimi"], details.Genres);
        Assert.Equal("FSK-16", details.OfficialRating);
        Assert.Equal(8.25, details.CommunityRating);
        Assert.Equal(TimeSpan.FromMinutes(45), details.Runtime);
        Assert.Equal(2019, details.EndYear);
        Assert.False(details.IsContinuing);
        Assert.Equal(["Studio Nord"], details.Studios);
        Assert.Equal(["Erika Regie"], details.Directors);
        Assert.Equal([new("Max Muster", "Kommissar"), new("Ohne Rolle", null)], details.Cast);

        var uri = Assert.Single(handler.Requests).RequestUri!.ToString();
        Assert.StartsWith("http://jellyfin:8096/Items?Ids=s1&", uri);
        Assert.Contains("Overview", uri);
        Assert.Contains("People", uri);
    }

    [Fact]
    public async Task GetDetails_AHugeOverviewWithoutClosingBrackets_IsCutShortQuickly()
    {
        var overview = new string('<', 200_000);
        var json = $$"""{"Items":[{"Id":"m1","Name":"Film","Overview":"{{overview}}"}]}""";
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, json)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var details = await client.GetDetailsAsync("http://jellyfin:8096", "key", "m1");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"PlainText brauchte {watch.Elapsed}");
        Assert.True(details!.Overview!.Length <= 5001);
    }

    [Fact]
    public async Task GetDetails_ARunningSeries_HasNoEndYear_AndAtMostTenActors()
    {
        var people = string.Join(",", Enumerable.Range(1, 15).Select(i => $$"""{"Name":"Person {{i}}","Type":"Actor"}"""));
        var json = $$"""{"Items":[{"Id":"s1","Name":"Serie","Status":"Continuing","EndDate":"2024-01-01T00:00:00Z","People":[{{people}}]}]}""";
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, json)));

        var details = await client.GetDetailsAsync("http://jellyfin:8096", "key", "s1");

        Assert.NotNull(details);
        Assert.True(details.IsContinuing);
        Assert.Null(details.EndYear);
        Assert.Equal(10, details.Cast.Count);
        Assert.Empty(details.Genres);
        Assert.Null(details.Overview);
    }

    [Fact]
    public async Task GetDetails_UnknownItem_IsNull()
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK, """{"Items":[]}""")));

        Assert.Null(await client.GetDetailsAsync("http://jellyfin:8096", "key", "weg"));
    }

    [Fact]
    public async Task GetDetails_EscapesTheId()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, """{"Items":[]}""");
        var client = new JellyfinClient(new HttpClient(handler));

        await client.GetDetailsAsync("http://jellyfin:8096", "key", "a&Recursive=true");

        Assert.Contains("Ids=a%26Recursive%3Dtrue&", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetEpisodes_KeepsTheEpisodeTitles()
    {
        var client = new JellyfinClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.OK,
            """{"Items":[{"Name":"Pilot","ParentIndexNumber":1,"IndexNumber":1,"LocationType":"FileSystem"}]}""")));

        var episode = Assert.Single(await client.GetEpisodesAsync("http://jellyfin:8096", "key", "s1"));

        Assert.Equal("Pilot", episode.Title);
    }
}
