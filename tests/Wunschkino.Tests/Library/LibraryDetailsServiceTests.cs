using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Data;
using Wunschkino.Gaps;
using Wunschkino.Library;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;
using static Wunschkino.Tests.Helpers.JellyfinFixtures;

namespace Wunschkino.Tests.Library;

public class LibraryDetailsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly LibraryCache _library = new();
    private readonly ManualClock _clock = new();
    private readonly MemoryCache _cache;
    private readonly SettingsService _settings;
    private Func<HttpRequestMessage, HttpResponseMessage> _jellyfin = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
    private readonly FakeHttpHandler _handler;

    public LibraryDetailsServiceTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
        _settings = new SettingsService(_db, new EphemeralDataProtectionProvider(), NullLogger<SettingsService>.Instance);
        _handler = new FakeHttpHandler(request => _jellyfin(request));
        _library.Update([LibraryItem("m1", "Film"), LibraryItem("s1", "Serie", MediaKind.Series)], DateTime.UtcNow);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    private async Task<LibraryDetailsService> ServiceAsync()
    {
        await _settings.SetAsync(SettingKeys.JellyfinUrl, "http://jellyfin:8096", isProtected: false);
        await _settings.SetAsync(SettingKeys.JellyfinApiKey, "key", isProtected: true);
        return new LibraryDetailsService(_library, _settings, new JellyfinClient(new HttpClient(_handler)), _db, _cache,
            NullLogger<LibraryDetailsService>.Instance);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage SeriesResponses(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/Episodes", StringComparison.Ordinal)
            ? Json("""{"Items":[{"Name":"Pilot","ParentIndexNumber":1,"IndexNumber":1},{"Name":"Drei","ParentIndexNumber":1,"IndexNumber":3}]}""")
            : Json("""{"Items":[{"Id":"s1","Name":"Serie","Overview":"Worum es geht","Status":"Continuing"}]}""");

    [Fact]
    public async Task ATitleOutsideTheLibrary_IsNeverAskedFor()
    {
        var service = await ServiceAsync();

        var result = await service.GetAsync("irgendwas");

        Assert.Null(result.Details);
        Assert.NotNull(result.Error);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ASeries_ComesWithItsSeasonsAndMissingEpisodes_AndIsKeptForAWhile()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.SeriesGaps.Add(new SeriesGap
            {
                JellyfinId = "s1", Title = "Serie", HasResult = true, Status = GapStatus.Missing,
                MissingJson = SeriesGapSerializer.Serialize([new SeasonGap(1, [2], false)])
            });
            await db.SaveChangesAsync();
        }
        _jellyfin = SeriesResponses;
        var service = await ServiceAsync();

        var result = await service.GetAsync("s1");
        await service.GetAsync("s1");

        var details = Assert.IsType<LibraryDetails>(result.Details);
        Assert.Equal("Worum es geht", details.Info.Overview);
        var season = Assert.Single(details.Seasons);
        Assert.Equal(["Pilot", "Drei"], season.Episodes.Select(e => e.Title));
        Assert.Equal([2], season.Missing);
        Assert.False(details.MissingIsUncertain);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task AMovie_HasNoSeasons_AndAsksJellyfinOnlyOnce()
    {
        _jellyfin = _ => Json("""{"Items":[{"Id":"m1","Name":"Film","RunTimeTicks":72000000000}]}""");
        var service = await ServiceAsync();

        var details = (await service.GetAsync("m1")).Details;

        Assert.NotNull(details);
        Assert.Empty(details.Seasons);
        Assert.Equal(TimeSpan.FromHours(2), details.Info.Runtime);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task AFailure_IsReported_AndRememberedOnlyBriefly()
    {
        var service = await ServiceAsync();

        var failed = await service.GetAsync("m1");
        _jellyfin = _ => Json("""{"Items":[{"Id":"m1","Name":"Film"}]}""");
        var again = await service.GetAsync("m1");
        _clock.UtcNow += LibraryDetailsService.FailureLifetime + TimeSpan.FromSeconds(1);
        var retried = await service.GetAsync("m1");

        Assert.Contains("HTTP 500", failed.Error);
        Assert.Equal(failed.Error, again.Error);
        Assert.NotNull(retried.Details);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task WithoutJellyfinSettings_SaysSo()
    {
        var service = new LibraryDetailsService(_library, _settings, new JellyfinClient(new HttpClient(_handler)), _db, _cache,
            NullLogger<LibraryDetailsService>.Instance);

        var result = await service.GetAsync("m1");

        Assert.NotNull(result.Error);
        Assert.Empty(_handler.Requests);
    }
}

internal sealed class ManualClock : Microsoft.Extensions.Internal.ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}
