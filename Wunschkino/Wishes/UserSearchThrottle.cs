namespace Wunschkino.Wishes;

/// <summary>
/// Begrenzt die TMDB-Suchen je Nutzer pro Minute: Ein Nutzer, der die Suche in Dauerschleife schickt, soll weder TMDB belasten
/// noch dem Betreiber das Kontingent aufbrauchen. Das Limit stellt der Admin ein (0 = keins).
/// </summary>
public sealed class UserSearchThrottle(TimeProvider time)
{
    // Ab dieser Zahl bekannter Nutzer werden abgelaufene Zähler aufgeräumt, damit der Speicher nicht unbegrenzt wächst.
    private const int SweepThreshold = 100;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<int, Queue<DateTimeOffset>> _requestsByUser = new();
    private readonly Lock _gate = new();

    /// <summary>Zahl der Nutzer, für die gerade ein Zähler im Speicher liegt (für Tests des Aufräumens).</summary>
    public int TrackedUsers
    {
        get
        {
            lock (_gate)
                return _requestsByUser.Count;
        }
    }

    /// <returns>null, wenn die Suche erlaubt ist (und gezählt wird), sonst die Wartezeit bis zur nächsten.</returns>
    public TimeSpan? TryAcquire(int userId, int limitPerMinute)
    {
        if (limitPerMinute <= 0)
            return null;

        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_requestsByUser.TryGetValue(userId, out var own))
            {
                if (_requestsByUser.Count >= SweepThreshold)
                    SweepExpiredUsers(now);
                own = _requestsByUser[userId] = new Queue<DateTimeOffset>();
            }
            DropExpired(own, now);

            if (own.Count >= limitPerMinute)
                return Window - (now - own.Peek());

            own.Enqueue(now);
            return null;
        }
    }

    private static void DropExpired(Queue<DateTimeOffset> requests, DateTimeOffset now)
    {
        while (requests.Count > 0 && now - requests.Peek() >= Window)
            requests.Dequeue();
    }

    private void SweepExpiredUsers(DateTimeOffset now)
    {
        foreach (var (userId, requests) in _requestsByUser.ToList())
        {
            DropExpired(requests, now);
            if (requests.Count == 0)
                _requestsByUser.Remove(userId);
        }
    }
}
