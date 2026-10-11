using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wunschkino.Anime;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Anime;

public class AnimeListStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void NothingStoredYet_ReturnsNulls()
    {
        var store = new AnimeListStore(_directory.Path);

        Assert.Null(store.ReadList());
        Assert.Null(store.ReadETag());
        Assert.Null(store.LastWriteUtc);
    }

    [Fact]
    public void Write_RoundTrips_AndOverwritesPreviousContent()
    {
        var store = new AnimeListStore(_directory.Path);

        store.Write("[1]"u8.ToArray(), "\"abc\"");
        store.Write("[2]"u8.ToArray(), "\"def\"");

        Assert.Equal("[2]", Encoding.UTF8.GetString(store.ReadList()!));
        Assert.Equal("\"def\"", store.ReadETag());
        Assert.NotNull(store.LastWriteUtc);
        Assert.Empty(Directory.GetFiles(_directory.Path, "*.tmp"));
    }

    [Fact]
    public void Write_WithoutETag_RemovesTheOldOne()
    {
        var store = new AnimeListStore(_directory.Path);
        store.Write("[1]"u8.ToArray(), "\"abc\"");

        store.Write("[2]"u8.ToArray(), null);

        Assert.Null(store.ReadETag());
    }
}

public class AnimeListClientTests
{
    [Fact]
    public async Task Download_Success_ReturnsContentAndETag()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("[]"u8.ToArray()) };
            response.Headers.ETag = new("\"v1\"");
            return response;
        });

        var download = await new AnimeListClient(new HttpClient(handler)).DownloadAsync(null);

        Assert.False(download.NotModified);
        Assert.Equal("[]", Encoding.UTF8.GetString(download.Content!));
        Assert.Equal("\"v1\"", download.ETag);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(AnimeListClient.ListUrl, request.RequestUri);
        Assert.False(request.Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task Download_SendsKnownETag_AndReportsNotModified()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var download = await new AnimeListClient(new HttpClient(handler)).DownloadAsync("\"v1\"");

        Assert.True(download.NotModified);
        Assert.Null(download.Content);
        Assert.Equal("\"v1\"", handler.Requests[0].Headers.GetValues("If-None-Match").Single());
    }

    [Theory]
    [InlineData("kein-etag")]
    [InlineData("\"a\r\nX-Evil: 1\"")]
    [InlineData("")]
    public async Task Download_IgnoresETagsWithUnexpectedFormat(string etag)
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, "[]");

        await new AnimeListClient(new HttpClient(handler)).DownloadAsync(etag);

        Assert.False(handler.Requests[0].Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task Download_ServerError_Throws()
    {
        var client = new AnimeListClient(new HttpClient(FakeHttpHandler.Json(HttpStatusCode.InternalServerError)));

        var exception = await Assert.ThrowsAsync<AnimeListException>(() => client.DownloadAsync(null));

        Assert.Contains("HTTP 500", exception.Message);
    }

    [Fact]
    public async Task Download_AnnouncedSizeOverTheLimit_IsRefused()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var content = new ByteArrayContent([1]);
            content.Headers.ContentLength = AnimeListClient.MaxBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        await Assert.ThrowsAsync<AnimeListException>(() => new AnimeListClient(new HttpClient(handler)).DownloadAsync(null));
    }

    [Fact]
    public async Task Download_BodyThatNeverArrives_TimesOutInsteadOfHangingForever()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) });
        var client = new AnimeListClient(new HttpClient(handler)) { DownloadTimeout = TimeSpan.FromMilliseconds(200) };

        var exception = await Assert.ThrowsAsync<AnimeListException>(() => client.DownloadAsync(null));

        Assert.Contains("zu langsam", exception.Message);
    }

    [Fact]
    public async Task Download_CancelledByCaller_PropagatesTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = new AnimeListClient(new HttpClient(new FakeHttpHandler(_ => throw new OperationCanceledException(cts.Token))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(null, cts.Token));
    }

    /// <summary>Liefert nie Daten und endet nur durch Abbruch – wie ein Server, der mitten im Body hängt.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task Download_Unreachable_ThrowsReadableException()
    {
        var client = new AnimeListClient(new HttpClient(FakeHttpHandler.Throws(new HttpRequestException("down"))));

        var exception = await Assert.ThrowsAsync<AnimeListException>(() => client.DownloadAsync(null));

        Assert.Contains("nicht erreichbar", exception.Message);
    }
}

public class AnimeIdMappingUpdaterTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly AnimeIdMapping _mapping = new();
    private readonly AnimeListStore _store;

    public AnimeIdMappingUpdaterTests() => _store = new AnimeListStore(_directory.Path);

    public void Dispose() => _directory.Dispose();

    private AnimeIdMappingUpdater CreateUpdater(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AnimeListClient(new HttpClient(handler)));
        return new AnimeIdMappingUpdater(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _mapping,
            _store,
            TimeProvider.System,
            NullLogger<AnimeIdMappingUpdater>.Instance);
    }

    private static string ListJson(int count) =>
        "[" + string.Join(',', Enumerable.Range(1, count).Select(i => "{\"type\":\"TV\",\"anilist_id\":" + i + ",\"themoviedb_id\":{\"tv\":" + (i + 1000) + "}}")) + "]";

    private static HttpResponseMessage Ok(string json, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = new(etag);
        return response;
    }

    [Fact]
    public async Task Refresh_ValidList_UpdatesMappingAndStoresFileWithETag()
    {
        var updater = CreateUpdater(new FakeHttpHandler(_ => Ok(ListJson(150), "\"v1\"")));

        var wait = await updater.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(AnimeIdMappingUpdater.RefreshInterval, wait);
        Assert.Equal(150, _mapping.Count);
        Assert.Equal(1001, _mapping.Find(1)!.TmdbId);
        Assert.NotNull(_store.ReadList());
        Assert.Equal("\"v1\"", _store.ReadETag());
    }

    [Fact]
    public async Task RequestRefresh_StartsAnImmediateRun_InsteadOfWaitingForTheNextDay()
    {
        var downloads = 0;
        var updater = CreateUpdater(new FakeHttpHandler(_ =>
        {
            Interlocked.Increment(ref downloads);
            return Ok(ListJson(150), "\"v1\"");
        }));

        await updater.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref downloads) >= 1);
            updater.RequestRefresh();
            await WaitUntilAsync(() => Volatile.Read(ref downloads) >= 2);
        }
        finally
        {
            await updater.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, Volatile.Read(ref downloads));
    }

    [Fact]
    public async Task RequestRefresh_CalledSeveralTimesBeforeTheRun_CausesExactlyOneExtraRun()
    {
        var downloads = 0;
        var updater = CreateUpdater(new FakeHttpHandler(_ =>
        {
            Interlocked.Increment(ref downloads);
            return Ok(ListJson(150), "\"v1\"");
        }));
        updater.RequestRefresh();
        updater.RequestRefresh();
        updater.RequestRefresh();

        await updater.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref downloads) >= 2);
            await Task.Delay(300);
        }
        finally
        {
            await updater.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, Volatile.Read(ref downloads));
    }

    [Fact]
    public async Task Refresh_CountsRuns_AndReportsWhetherTheLastOneSucceeded()
    {
        var fail = false;
        var updater = CreateUpdater(new FakeHttpHandler(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Ok(ListJson(150), "\"v1\"")));

        await updater.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(1, updater.RunsCompleted);
        Assert.True(updater.LastRunSucceeded);

        fail = true;
        await updater.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(2, updater.RunsCompleted);
        Assert.False(updater.LastRunSucceeded);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Der erwartete Lauf des Updaters blieb aus.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Refresh_NotModified_KeepsEntriesAndSendsStoredETag()
    {
        _store.Write(Encoding.UTF8.GetBytes(ListJson(150)), "\"v1\"");
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var updater = CreateUpdater(handler);
        updater.LoadFromDisk();

        var wait = await updater.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(AnimeIdMappingUpdater.RefreshInterval, wait);
        Assert.Equal(150, _mapping.Count);
        Assert.Equal("\"v1\"", handler.Requests[0].Headers.GetValues("If-None-Match").Single());
        Assert.True(_mapping.CheckedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Refresh_ImplausiblySmallList_IsDiscardedAndKeepsThePreviousState()
    {
        var updater = CreateUpdater(new FakeHttpHandler(_ => Ok(ListJson(150), "\"v1\"")));
        await updater.RefreshOnceAsync(CancellationToken.None);
        var broken = CreateUpdater(new FakeHttpHandler(_ => Ok("[]", "\"v2\"")));

        var wait = await broken.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(AnimeIdMappingUpdater.RetryInterval, wait);
        Assert.Equal(150, _mapping.Count);
        Assert.Equal("\"v1\"", _store.ReadETag());
    }

    [Fact]
    public async Task Refresh_ListShrinkingToLessThanHalf_IsDiscardedAndKeepsThePreviousState()
    {
        await CreateUpdater(new FakeHttpHandler(_ => Ok(ListJson(400), "\"v1\""))).RefreshOnceAsync(CancellationToken.None);
        var shrunk = CreateUpdater(new FakeHttpHandler(_ => Ok(ListJson(150), "\"v2\"")));

        var wait = await shrunk.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(AnimeIdMappingUpdater.RetryInterval, wait);
        Assert.Equal(400, _mapping.Count);
        Assert.Equal("\"v1\"", _store.ReadETag());
    }

    [Fact]
    public async Task Refresh_ModeratelySmallerList_IsAccepted()
    {
        await CreateUpdater(new FakeHttpHandler(_ => Ok(ListJson(200), "\"v1\""))).RefreshOnceAsync(CancellationToken.None);

        await CreateUpdater(new FakeHttpHandler(_ => Ok(ListJson(150), "\"v2\""))).RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(150, _mapping.Count);
    }

    [Theory]
    [InlineData("download")]
    [InlineData("garbage")]
    public async Task Refresh_Failure_KeepsStateAndRetriesLater(string failure)
    {
        var updater = CreateUpdater(failure == "download"
            ? FakeHttpHandler.Throws(new HttpRequestException("down"))
            : FakeHttpHandler.Json(HttpStatusCode.OK, "<html>"));

        var wait = await updater.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(AnimeIdMappingUpdater.RetryInterval, wait);
        Assert.Equal(0, _mapping.Count);
        Assert.Null(_store.ReadList());
    }

    [Fact]
    public async Task Refresh_AfterACorruptFileOnDisk_IgnoresTheStoredETagSoTheListIsReloaded()
    {
        _store.Write("kaputt"u8.ToArray(), "\"v1\"");
        var handler = new FakeHttpHandler(_ => Ok(ListJson(150), "\"v2\""));
        var updater = CreateUpdater(handler);
        updater.LoadFromDisk();

        await updater.RefreshOnceAsync(CancellationToken.None);

        Assert.False(handler.Requests[0].Headers.Contains("If-None-Match"));
        Assert.Equal(150, _mapping.Count);
        Assert.Equal("\"v2\"", _store.ReadETag());
    }

    [Fact]
    public async Task Refresh_UnexpectedException_IsContainedAndRetriedLater()
    {
        var updater = CreateUpdater(FakeHttpHandler.Throws(new InvalidOperationException("unerwartet")));

        var wait = await updater.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(AnimeIdMappingUpdater.RetryInterval, wait);
    }

    [Fact]
    public async Task Refresh_ShutdownRequest_StillPropagatesTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var updater = CreateUpdater(new FakeHttpHandler(_ => throw new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.RefreshOnceAsync(cts.Token));
    }

    [Fact]
    public void LoadFromDisk_UsesTheStoredList_AndIgnoresACorruptOne()
    {
        _store.Write(Encoding.UTF8.GetBytes(ListJson(150)), null);
        CreateUpdater(FakeHttpHandler.Json(HttpStatusCode.OK)).LoadFromDisk();
        Assert.Equal(150, _mapping.Count);

        var other = new AnimeIdMapping();
        _store.Write("kaputt"u8.ToArray(), null);
        new AnimeIdMappingUpdater(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            other, _store, TimeProvider.System, NullLogger<AnimeIdMappingUpdater>.Instance).LoadFromDisk();
        Assert.Equal(0, other.Count);
    }
}
