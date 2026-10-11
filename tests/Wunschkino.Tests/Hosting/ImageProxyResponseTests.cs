using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wunschkino.Hosting;

namespace Wunschkino.Tests.Hosting;

public class ImageProxyResponseTests
{
    private static HttpResponseMessage Upstream(byte[] body, string? contentType, bool announceLength = true)
    {
        HttpContent content = announceLength ? new ByteArrayContent(body) : new StreamContent(new NonSeekableStream(body));
        if (contentType is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage { Content = content };
    }

    private static DefaultHttpContext Context()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
    }

    [Fact]
    public async Task Buffering_ReturnsTheBytesAndType_AndDisposesTheUpstream()
    {
        var upstream = Upstream([1, 2, 3], "image/PNG");

        var image = await ImageProxyResponse.BufferAsync(upstream, CancellationToken.None);

        Assert.NotNull(image);
        Assert.Equal([1, 2, 3], image.Bytes);
        Assert.Equal("image/png", image.ContentType);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData(null)]
    public async Task Buffering_RejectsAnythingButRasterImages(string? contentType) =>
        Assert.Null(await ImageProxyResponse.BufferAsync(Upstream([1], contentType), CancellationToken.None));

    [Fact]
    public async Task Buffering_RejectsTooLargeBodies_EvenWithoutAnAnnouncedLength() =>
        Assert.Null(await ImageProxyResponse.BufferAsync(
            Upstream(new byte[ImageProxyResponse.MaxBytes + 1], "image/jpeg", announceLength: false), CancellationToken.None));

    [Fact]
    public async Task APosterCache_ServesTheSecondRequestFromMemory_AndRespectsItsSize()
    {
        var cache = new PosterCache(new WunschkinoOptions { PosterCacheMegabytes = 1 });
        var image = new CachedImage([1, 2, 3], "image/jpeg");
        Assert.False(cache.TryGet("a:300", out _));

        cache.Set("a:300", image);
        cache.Set("zu-gross", new CachedImage(new byte[2_000_000], "image/jpeg"));

        Assert.True(cache.TryGet("a:300", out var hit));
        Assert.Same(image, hit);
        Assert.False(cache.TryGet("zu-gross", out _));

        var context = Context();
        await ImageProxyResponse.FromCache(image, context).ExecuteAsync(context);
        Assert.Equal("image/jpeg", context.Response.ContentType);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Equal([1, 2, 3], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public void APosterCacheOfZeroMegabytes_StoresNothing()
    {
        var cache = new PosterCache(new WunschkinoOptions { PosterCacheMegabytes = 0 });

        cache.Set("a:300", new CachedImage([1], "image/jpeg"));

        Assert.False(cache.TryGet("a:300", out _));
    }

    [Fact]
    public async Task PassesImagesThrough()
    {
        var context = Context();

        var result = await ImageProxyResponse.ToResultAsync(Upstream([1, 2, 3], "image/jpeg"), context);
        await result.ExecuteAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("image/jpeg", context.Response.ContentType);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Equal([1, 2, 3], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("image/svg+xml")]
    [InlineData(null)]
    public async Task RejectsNonImages(string? contentType)
    {
        var context = Context();

        var result = await ImageProxyResponse.ToResultAsync(Upstream([1], contentType), context);
        await result.ExecuteAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
    }

    [Fact]
    public async Task RejectsAnnouncedOversizedImages()
    {
        var context = Context();

        var result = await ImageProxyResponse.ToResultAsync(Upstream(new byte[ImageProxyResponse.MaxBytes + 1], "image/png"), context);
        await result.ExecuteAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
    }

    [Fact]
    public async Task AbortsOversizedImagesWithoutAnnouncedLength()
    {
        var context = Context();

        var result = await ImageProxyResponse.ToResultAsync(
            Upstream(new byte[ImageProxyResponse.MaxBytes + 1], "image/png", announceLength: false), context);

        await Assert.ThrowsAsync<IOException>(() => result.ExecuteAsync(context));
    }

    [Fact]
    public async Task LengthLimitedStream_AllowsExactlyTheLimit()
    {
        await using var stream = new LengthLimitedStream(new MemoryStream(new byte[10]), 10);

        var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal(10, copy.Length);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
