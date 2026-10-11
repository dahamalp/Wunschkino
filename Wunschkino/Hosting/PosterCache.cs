using Microsoft.Extensions.Caching.Memory;

namespace Wunschkino.Hosting;

public sealed record CachedImage(byte[] Bytes, string ContentType);

/// <summary>
/// Hält die zuletzt geladenen Jellyfin-Poster im Arbeitsspeicher. Ohne diesen Speicher fragt jeder Aufruf von /posters/… den
/// Jellyfin-Server neu; ein angemeldeter Nutzer (oder ein gestohlenes Cookie) könnte ihn mit Wiederholungen belasten. Die Größe ist
/// begrenzt (<see cref="WunschkinoOptions.PosterCacheMegabytes"/>, 0 schaltet den Speicher ab); alle Nutzer sehen dieselbe
/// Bibliothek, deshalb teilen sich die Einträge keine persönlichen Daten.
/// </summary>
public sealed class PosterCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private readonly MemoryCache? _cache;

    public PosterCache(WunschkinoOptions options)
    {
        if (options.PosterCacheMegabytes > 0)
            _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.PosterCacheMegabytes * 1_000_000L });
    }

    public bool TryGet(string key, out CachedImage? image)
    {
        image = null;
        return _cache is not null && _cache.TryGetValue(key, out image);
    }

    public void Set(string key, CachedImage image) =>
        _cache?.Set(key, image, new MemoryCacheEntryOptions { Size = image.Bytes.Length, AbsoluteExpirationRelativeToNow = Lifetime });
}
