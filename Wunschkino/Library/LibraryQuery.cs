namespace Wunschkino.Library;

public enum LibrarySort
{
    Title,
    YearDescending,
    RecentlyAdded
}

public static class LibraryQuery
{
    public static IEnumerable<LibraryItem> Apply(IEnumerable<LibraryItem> items, string? search, MediaKind? kind, LibrarySort sort)
    {
        var filtered = items;
        if (kind is { } wantedKind)
            filtered = filtered.Where(i => i.Kind == wantedKind);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filtered = filtered.Where(i => i.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                                           || i.Year?.ToString() == term);
        }

        return sort switch
        {
            LibrarySort.YearDescending => filtered.OrderByDescending(i => i.Year ?? 0).ThenBy(i => i.Title, NaturalTitleComparer.Instance),
            LibrarySort.RecentlyAdded => filtered.OrderByDescending(i => i.AddedUtc ?? DateTime.MinValue).ThenBy(i => i.Title, NaturalTitleComparer.Instance),
            _ => filtered.OrderBy(i => i.Title, NaturalTitleComparer.Instance)
        };
    }
}
