using Wunschkino.Library;
using static Wunschkino.Tests.Helpers.JellyfinFixtures;

namespace Wunschkino.Tests.Library;

public class LibraryCacheTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Lookups_FindItemsByTmdbPerKindAndByImdb()
    {
        var cache = new LibraryCache();
        cache.Update([
            LibraryItem("m1", "Film", MediaKind.Movie, tmdb: "1399", imdb: "tt0000001"),
            LibraryItem("s1", "Serie", MediaKind.Series, tmdb: "1400")
        ], Now);

        Assert.Equal("m1", cache.FindByTmdb(MediaKind.Movie, "1399")?.JellyfinId);
        Assert.Null(cache.FindByTmdb(MediaKind.Series, "1399"));
        Assert.Equal("s1", cache.FindByTmdb(MediaKind.Series, "1400")?.JellyfinId);
        Assert.Equal("m1", cache.FindByImdb("TT0000001")?.JellyfinId);
        Assert.True(cache.TryGet("m1", out _));
        Assert.False(cache.TryGet("unbekannt", out _));
    }

    [Fact]
    public void ReportError_KeepsLastItems()
    {
        var cache = new LibraryCache();
        cache.Update([LibraryItem("m1", "Film")], Now);

        cache.ReportError("Jellyfin ist nicht erreichbar.");

        Assert.Single(cache.Snapshot.Items);
        Assert.Equal(Now, cache.Snapshot.UpdatedUtc);
        Assert.Equal("Jellyfin ist nicht erreichbar.", cache.Snapshot.Error);
    }

    [Fact]
    public void Update_ClearsErrorAndNotifies()
    {
        var cache = new LibraryCache();
        var notifications = 0;
        cache.Changed += () => notifications++;
        cache.ReportError("Fehler");

        cache.Update([LibraryItem("m1", "Film")], Now);

        Assert.Null(cache.Snapshot.Error);
        Assert.Equal(2, notifications);
    }
}
