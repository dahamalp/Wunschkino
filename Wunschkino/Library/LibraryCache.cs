namespace Wunschkino.Library;

public sealed record LibrarySnapshot(IReadOnlyList<LibraryItem> Items, DateTime? UpdatedUtc, string? Error)
{
    public static readonly LibrarySnapshot Empty = new([], null, null);
}

/// <summary>
/// Jellyfin-Bestand im Speicher. Jeder Stand ist unveränderlich und wird als Ganzes ausgetauscht,
/// damit Leser nie einen halb aktualisierten Zustand sehen.
/// </summary>
public sealed class LibraryCache
{
    private State _state = new(LibrarySnapshot.Empty);

    public event Action? Changed;

    public LibrarySnapshot Snapshot => Volatile.Read(ref _state).Snapshot;

    public void Update(IReadOnlyList<LibraryItem> items, DateTime nowUtc)
    {
        Volatile.Write(ref _state, new State(new LibrarySnapshot(items, nowUtc, null)));
        Changed?.Invoke();
    }

    /// <summary>Behält den letzten erfolgreichen Bestand und vermerkt nur den Fehler.</summary>
    public void ReportError(string error)
    {
        var current = Volatile.Read(ref _state).Snapshot;
        Volatile.Write(ref _state, new State(current with { Error = error }));
        Changed?.Invoke();
    }

    public bool TryGet(string jellyfinId, out LibraryItem item) =>
        Volatile.Read(ref _state).ById.TryGetValue(jellyfinId, out item!);

    public LibraryItem? FindByTmdb(MediaKind kind, string tmdbId) =>
        Volatile.Read(ref _state).ByTmdb.GetValueOrDefault((kind, tmdbId));

    public LibraryItem? FindByImdb(string imdbId) =>
        Volatile.Read(ref _state).ByImdb.GetValueOrDefault(imdbId);

    /// <summary>Nur gefüllt, wenn Jellyfin ein AniList-Plugin hat, das die ID als „AniList“ ablegt.</summary>
    public LibraryItem? FindByAniList(string aniListId) =>
        Volatile.Read(ref _state).ByAniList.GetValueOrDefault(aniListId);

    private sealed class State
    {
        public State(LibrarySnapshot snapshot)
        {
            Snapshot = snapshot;
            foreach (var item in snapshot.Items)
            {
                ById.TryAdd(item.JellyfinId, item);
                if (item.TmdbId is { } tmdbId)
                    ByTmdb.TryAdd((item.Kind, tmdbId), item);
                if (item.ImdbId is { } imdbId)
                    ByImdb.TryAdd(imdbId, item);
                if (item.AniListId is { } aniListId)
                    ByAniList.TryAdd(aniListId, item);
            }
        }

        public LibrarySnapshot Snapshot { get; }
        public Dictionary<string, LibraryItem> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
        // TMDB-IDs sind nur je Typ eindeutig: Film 1399 und Serie 1399 sind verschiedene Titel.
        public Dictionary<(MediaKind, string), LibraryItem> ByTmdb { get; } = [];
        public Dictionary<string, LibraryItem> ByImdb { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LibraryItem> ByAniList { get; } = [];
    }
}
