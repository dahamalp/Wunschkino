namespace Wunschkino.Hosting;

/// <summary>
/// Gibt ein von Jellyfin, TMDB oder AniList geladenes Bild an den Browser weiter. Zu große Antworten und Antworten,
/// die kein Bild sind, werden verworfen, damit ein fehlerhafter oder fremder Server weder Speicher noch Leitung füllt.
/// </summary>
public static class ImageProxyResponse
{
    public const long MaxBytes = 5_000_000;

    // Nur Rasterbilder: Ein SVG kann Skript enthalten und liefe direkt aufgerufen unter der eigenen Herkunft.
    private static readonly HashSet<string> AllowedTypes = ["image/jpeg", "image/png", "image/webp", "image/gif", "image/avif"];

    /// <summary>Liest das Bild vollständig (für den Zwischenspeicher) und entsorgt die Antwort; null, wenn es kein erlaubtes Bild oder zu groß ist.</summary>
    public static async Task<CachedImage?> BufferAsync(HttpResponseMessage upstream, CancellationToken ct)
    {
        using (upstream)
        {
            var contentType = upstream.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (contentType is null || !AllowedTypes.Contains(contentType) || upstream.Content.Headers.ContentLength > MaxBytes)
                return null;

            try
            {
                await using var body = new LengthLimitedStream(await upstream.Content.ReadAsStreamAsync(ct), MaxBytes);
                using var buffer = new MemoryStream();
                await body.CopyToAsync(buffer, ct);
                return new CachedImage(buffer.ToArray(), contentType);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public static IResult FromCache(CachedImage image, HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, max-age=86400";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Bytes(image.Bytes, image.ContentType);
    }

    public static async Task<IResult> ToResultAsync(HttpResponseMessage upstream, HttpContext context)
    {
        context.Response.RegisterForDispose(upstream);
        var contentType = upstream.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (contentType is null || !AllowedTypes.Contains(contentType))
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        if (upstream.Content.Headers.ContentLength > MaxBytes)
            return Results.StatusCode(StatusCodes.Status502BadGateway);

        context.Response.Headers.CacheControl = "private, max-age=86400";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
        return Results.Stream(new LengthLimitedStream(body, MaxBytes), contentType);
    }
}

/// <summary>Liest höchstens <c>maxBytes</c>; darüber hinaus bricht das Lesen ab, auch wenn keine Länge angekündigt war.</summary>
public sealed class LengthLimitedStream(Stream inner, long maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes)
            throw new IOException($"Die Antwort ist größer als {maxBytes} Bytes.");
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
