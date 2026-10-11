using Wunschkino.Library;

namespace Wunschkino.Data;

public enum WishStatus
{
    Open = 0,
    Accepted = 1,
    Rejected = 2,
    Done = 3
}

/// <summary>Verhältnis eines Wunsches zum automatischen Abgleich mit dem Jellyfin-Bestand.</summary>
public enum WishAutoCompletion
{
    /// <summary>Der Abgleich darf den Wunsch erledigen, sobald der Titel in Jellyfin liegt.</summary>
    None = 0,

    /// <summary>„Erledigt“ stammt vom Abgleich, nicht von einem Admin.</summary>
    Completed = 1,

    /// <summary>Ein Admin hat den erledigten Wunsch wieder geöffnet; der Abgleich rührt ihn nicht mehr an.</summary>
    Suppressed = 2
}

public class Wish
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public MediaKind Kind { get; set; }
    public required string Title { get; set; }
    public int? Year { get; set; }
    public string? PosterPath { get; set; }
    public string? ImdbId { get; set; }

    /// <summary>Wann Titel, Jahr, Poster und IMDb-Nummer zuletzt von TMDB geholt wurden. Die TMDB-Bedingungen erlauben höchstens 6 Monate.</summary>
    public DateTime MetadataFetchedUtc { get; set; }
    public WishStatus Status { get; set; }
    public string? AdminComment { get; set; }

    public WishAutoCompletion AutoCompletion { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public List<WishVote> Votes { get; set; } = [];
}

/// <summary>Eine Stimme für einen Wunsch. Der Ersteller gibt die erste ab, jeder weitere Nutzer per „+1“.</summary>
public class WishVote
{
    public int WishId { get; set; }
    public int UserId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedUtc { get; set; }

    public Wish? Wish { get; set; }
    public User? User { get; set; }
}
