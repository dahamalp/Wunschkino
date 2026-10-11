namespace Wunschkino.Gaps;

/// <param name="ScanRequested">Ein Neu-Scan aller Serien ist angefordert, der nächste Lauf holt ihn ab.</param>
/// <param name="Running">Ein Prüflauf ist gerade im Gang.</param>
/// <param name="Done">Wie viele Serien dieser Lauf schon bearbeitet hat.</param>
/// <param name="Total">Wie viele Serien dieser Lauf insgesamt bearbeitet.</param>
public sealed record SeriesGapProgressSnapshot(bool ScanRequested, bool Running, int Done, int Total)
{
    public bool Busy => ScanRequested || Running;
}

/// <summary>
/// Gemeinsamer Zustand zwischen Prüf-Dienst und Admin-Seite: ob ein Neu-Scan aller Serien angefordert ist und wie weit ein Lauf ist.
/// Lebt im Speicher; nach einem Neustart der App ist nichts mehr angefordert.
/// </summary>
public sealed class SeriesGapProgress(TimeProvider time)
{
    /// <summary>So lange wartet eine Anforderung auf einen Lauf. Danach gilt sie als verfallen, damit die Seite nie ewig „beschäftigt“ zeigt (etwa wenn Jellyfin nicht eingerichtet ist).</summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private DateTimeOffset? _requestedAt;
    private bool _running;
    private int _done;
    private int _total;

    public SeriesGapProgressSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                ExpireRequest();
                return new SeriesGapProgressSnapshot(_requestedAt is not null, _running, _done, _total);
            }
        }
    }

    public void RequestFullScan()
    {
        lock (_gate)
            _requestedAt = time.GetUtcNow();
    }

    /// <summary>Beginnt einen Lauf. Anfordern und Starten wechseln in einem Schritt, damit die Seite dazwischen nie „fertig“ sieht.</summary>
    /// <returns>true, wenn für diesen Lauf ein Neu-Scan aller Serien angefordert war.</returns>
    public bool BeginRun()
    {
        lock (_gate)
        {
            ExpireRequest();
            var fullScan = _requestedAt is not null;
            _requestedAt = null;
            _running = true;
            _done = 0;
            _total = 0;
            return fullScan;
        }
    }

    public void SetTotal(int total)
    {
        lock (_gate)
            _total = total;
    }

    public void Advance()
    {
        lock (_gate)
            _done++;
    }

    public void Finish()
    {
        lock (_gate)
            _running = false;
    }

    private void ExpireRequest()
    {
        if (_requestedAt is { } requestedAt && time.GetUtcNow() - requestedAt > RequestLifetime)
            _requestedAt = null;
    }
}
