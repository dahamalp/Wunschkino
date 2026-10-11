using System.Text;
using System.Text.Json;
using Wunschkino.Anime;
using Wunschkino.Library;

namespace Wunschkino.Tests.Anime;

public class AnimeIdMappingTests
{
    private static IReadOnlyDictionary<int, AnimeMapping> Parse(string json) =>
        AnimeIdMapping.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_ReadsTvMoviesAndImdbIdsInTheRealListFormat()
    {
        var entries = Parse("""
            [
              {"type":"TV","anilist_id":290,"mal_id":290,"imdb_id":["tt0286390"],"themoviedb_id":{"tv":26209},"tvdb_id":72025,"season":{"tvdb":1,"tmdb":1}},
              {"type":"MOVIE","anilist_id":15039,"imdb_id":["tt2000001","tt2000002"],"themoviedb_id":{"movie":[128,129]}}
            ]
            """);

        Assert.Equal(new AnimeMapping(290, MediaKind.Series, 26209, ["tt0286390"]), entries[290], new MappingComparer());
        var movie = entries[15039];
        Assert.Equal(MediaKind.Movie, movie.Kind);
        Assert.Equal(128, movie.TmdbId);
        Assert.Equal(["tt2000001", "tt2000002"], movie.ImdbIds);
        Assert.True(movie.HasTmdb);
    }

    [Fact]
    public void Parse_KeepsEntriesWithOnlyAnImdbId_ButWithoutTmdbMapping()
    {
        var entries = Parse("""[{"type":"OVA","anilist_id":5,"imdb_id":["tt0102847"]}]""");

        var entry = entries[5];
        Assert.False(entry.HasTmdb);
        Assert.Null(entry.Kind);
        Assert.Equal(["tt0102847"], entry.ImdbIds);
    }

    [Fact]
    public void Parse_DropsEntriesWithoutAniListIdOrWithoutAnyUsefulId()
    {
        var entries = Parse("""
            [
              {"type":"TV","themoviedb_id":{"tv":1}},
              {"type":"TV","anilist_id":2,"mal_id":2},
              {"type":"TV","anilist_id":0,"themoviedb_id":{"tv":3}},
              {"type":"TV","anilist_id":4,"themoviedb_id":{"tv":4}}
            ]
            """);

        Assert.Equal([4], entries.Keys);
    }

    [Fact]
    public void Parse_DropsImplausiblyLargeIds()
    {
        var entries = Parse("""
            [
              {"type":"TV","anilist_id":2000000000,"themoviedb_id":{"tv":1}},
              {"type":"TV","anilist_id":5,"themoviedb_id":{"tv":2000000000}},
              {"type":"MOVIE","anilist_id":6,"themoviedb_id":{"movie":[2000000000, 77]}},
              {"type":"TV","anilist_id":7,"themoviedb_id":{"tv":7}}
            ]
            """);

        Assert.Equal([6, 7], entries.Keys.Order());
        Assert.Equal(77, entries[6].TmdbId);
    }

    [Fact]
    public void Parse_ToleratesUnexpectedFieldShapes()
    {
        var entries = Parse("""
            [
              "kaputt",
              {"anilist_id":"7","themoviedb_id":{"tv":7}},
              {"anilist_id":8,"themoviedb_id":"x","imdb_id":"tt0000008"},
              {"anilist_id":9,"themoviedb_id":{"tv":"9","movie":["a",-1]},"imdb_id":["kein-imdb","tt1234567"]},
              {"anilist_id":10,"themoviedb_id":{"tv":10}}
            ]
            """);

        Assert.Equal([9, 10], entries.Keys.Order());
        Assert.False(entries[9].HasTmdb);
        Assert.Equal(["tt1234567"], entries[9].ImdbIds);
        Assert.Equal(10, entries[10].TmdbId);
    }

    [Fact]
    public void Parse_PrefersMovieIdForMovieTypeAndTvIdOtherwise()
    {
        var entries = Parse("""
            [
              {"type":"MOVIE","anilist_id":1,"themoviedb_id":{"tv":100,"movie":[200]}},
              {"type":"TV","anilist_id":2,"themoviedb_id":{"tv":100,"movie":[200]}}
            ]
            """);

        Assert.Equal((MediaKind.Movie, 200), (entries[1].Kind!.Value, entries[1].TmdbId!.Value));
        Assert.Equal((MediaKind.Series, 100), (entries[2].Kind!.Value, entries[2].TmdbId!.Value));
    }

    [Fact]
    public void Parse_FirstEntryWinsForDuplicateAniListIds()
    {
        var entries = Parse("""[{"anilist_id":1,"themoviedb_id":{"tv":10}},{"anilist_id":1,"themoviedb_id":{"tv":20}}]""");

        Assert.Equal(10, entries[1].TmdbId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"text\"")]
    [InlineData("<html>")]
    public void Parse_RejectsDocumentsThatAreNotAnArray(string json) =>
        Assert.ThrowsAny<JsonException>(() => Parse(json));

    [Fact]
    public void Find_ReturnsNullBeforeAnyListWasLoaded_AndAfterReplaceTheNewEntries()
    {
        var mapping = new AnimeIdMapping();
        Assert.Null(mapping.Find(1));
        Assert.Equal(0, mapping.Count);
        Assert.Null(mapping.CheckedUtc);

        var checkedAt = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);
        mapping.Replace(Parse("""[{"anilist_id":1,"themoviedb_id":{"tv":10}}]"""), checkedAt);

        Assert.Equal(10, mapping.Find(1)!.TmdbId);
        Assert.Equal(1, mapping.Count);
        Assert.Equal(checkedAt, mapping.CheckedUtc);
    }

    [Fact]
    public void MarkChecked_UpdatesOnlyTheTimestamp()
    {
        var mapping = new AnimeIdMapping();
        mapping.Replace(Parse("""[{"anilist_id":1,"themoviedb_id":{"tv":10}}]"""), DateTime.UtcNow.AddDays(-1));
        var later = DateTime.UtcNow;

        mapping.MarkChecked(later);

        Assert.Equal(later, mapping.CheckedUtc);
        Assert.Equal(10, mapping.Find(1)!.TmdbId);
    }

    /// <summary>Records vergleichen Listen per Referenz, deshalb vergleichen wir die Felder selbst.</summary>
    private sealed class MappingComparer : IEqualityComparer<AnimeMapping>
    {
        public bool Equals(AnimeMapping? x, AnimeMapping? y) =>
            x is not null && y is not null
            && x.AniListId == y.AniListId && x.Kind == y.Kind && x.TmdbId == y.TmdbId && x.ImdbIds.SequenceEqual(y.ImdbIds);

        public int GetHashCode(AnimeMapping obj) => obj.AniListId;
    }
}
