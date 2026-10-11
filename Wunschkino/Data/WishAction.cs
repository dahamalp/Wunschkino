namespace Wunschkino.Data;

/// <summary>
/// Eine gezählte Wunsch-Handlung eines Nutzers (neuer Wunsch oder „+1“) für das Tageslimit. Eigene Tabelle statt der Stimmen: Wer eine
/// Stimme zurückzieht, löscht sie, und das Limit ließe sich sonst durch Zurückziehen und erneutes Wünschen unterlaufen.
/// Die Zeilen werden nach zwei Tagen gelöscht.
/// </summary>
public class WishAction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public DateTime TimeUtc { get; set; }
}
