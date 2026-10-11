using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Wunschkino.Data;
using Wunschkino.Hosting;

namespace Wunschkino.Auth;

public sealed record IpBlockStatus(bool IsBlocked, DateTime? UntilUtc, bool IsPermanent)
{
    public static readonly IpBlockStatus Open = new(false, null, false);
}

/// <summary>
/// Sperrt Client-Adressen nach Fehlversuchen in Stufen: je 3 Fehlversuche 1 Minute, 5 Minuten, 15 Minuten, 1 Stunde, 24 Stunden,
/// danach dauerhaft, bis ein Admin entsperrt. Eine erfolgreiche Anmeldung senkt den Zähler der Adresse nur um eins (ein Angreifer
/// mit eigenem Konto soll sich den Zähler nicht zurücksetzen können); 30 Tage ohne Fehlversuch setzen ihn ganz zurück. Die Adresse des Servers selbst und die vertrauten Proxys werden nie gesperrt: Sonst könnte eine falsch
/// eingestellte Proxy-Adresse alle Nutzer zugleich aussperren.
/// </summary>
public sealed class IpBlockService(
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider time,
    TrustedProxies trustedProxies,
    ILogger<IpBlockService> logger,
    AuditLog? audit = null)
{
    public const int FailuresPerStage = 3;
    public static readonly IReadOnlyList<TimeSpan> Stages =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(24)];
    public static readonly TimeSpan Decay = TimeSpan.FromDays(30);

    // Jede Anfrage fragt die Sperren ab; der Speicher spart die Datenbank. Die Wartezeit begrenzt zugleich, wie lange es dauert,
    // bis eine Entsperrung per Konsole (anderer Prozess) in der laufenden App ankommt.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IpBlockStatus> _active = new();
    private DateTime _loadedAtUtc;
    private DateTime _lastCleanupUtc;

    /// <summary>Mehr Zeilen zeigen Admin-Seite und Konsole nicht; dauerhaft gesperrte und neueste zuerst.</summary>
    public const int MaxListed = 500;

    public bool IsExempt(IPAddress? remote, IPAddress? local)
    {
        if (remote is null || ClientAddress.IsLocal(remote, local))
            return true;

        var address = Unmap(remote);
        return trustedProxies.Addresses.Any(proxy => Unmap(proxy).Equals(address))
               || trustedProxies.Networks.Any(network => network.Contains(address));
    }

    public async Task<IpBlockStatus> CheckAsync(IPAddress? remote, IPAddress? local, CancellationToken ct = default)
    {
        if (IsExempt(remote, local))
            return IpBlockStatus.Open;

        await RefreshIfStaleAsync(ct);
        var now = Now();
        return _active.TryGetValue(ClientAddress.PartitionKey(remote), out var status) && IsActive(status, now)
            ? status
            : IpBlockStatus.Open;
    }

    /// <summary>Zählt einen Fehlversuch. Versuche während einer laufenden Sperre zählen nicht mit.</summary>
    public async Task<IpBlockStatus> RegisterFailureAsync(IPAddress? remote, IPAddress? local, CancellationToken ct = default)
    {
        if (IsExempt(remote, local))
            return IpBlockStatus.Open;

        try
        {
            return await RegisterFailureLockedAsync(remote, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Der Konsolenbefehl (ein anderer Prozess) hat die Zeile zwischen Lesen und Speichern gelöscht: noch einmal von vorn.
            return await RegisterFailureLockedAsync(remote, ct);
        }
    }

    private async Task<IpBlockStatus> RegisterFailureLockedAsync(IPAddress? remote, CancellationToken ct)
    {
        var key = ClientAddress.PartitionKey(remote);
        await _writeLock.WaitAsync(ct);
        try
        {
            var now = Now();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.IpBlocks.SingleOrDefaultAsync(b => b.Address == key, ct);
            if (row is not null && ToStatus(row, now) is { IsBlocked: true } running)
                return running;

            if (row is null)
            {
                row = new IpBlock { Address = key };
                db.IpBlocks.Add(row);
            }
            else if (now - row.LastFailureUtc > Decay)
            {
                row.Failures = 0;
                row.BlockedUntilUtc = null;
            }

            row.Failures++;
            row.LastFailureUtc = now;
            if (row.Failures % FailuresPerStage == 0)
                ApplyStage(row, now);
            await db.SaveChangesAsync(ct);

            var status = ToStatus(row, now);
            SetCached(key, status);
            if (status.IsBlocked)
            {
                logger.LogWarning("IpBlocked ip={Address} failures={Failures} until={Until}",
                    key, row.Failures, status.IsPermanent ? "permanent" : status.UntilUtc!.Value.ToString("O"));
                if (audit is not null)
                {
                    await audit.WriteAsync(AuditActions.IpBlocked, null, null, key,
                        $"nach {row.Failures} Fehlversuchen, {(status.IsPermanent ? "dauerhaft" : $"bis {status.UntilUtc!.Value:yyyy-MM-dd HH:mm} UTC")}", ct: ct);
                }
            }
            return status;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Nach einer erfolgreichen Anmeldung sinkt der Zähler um eins. Ein Löschen wäre ein Schlupfloch: Wer ein eigenes Konto hat,
    /// könnte zwischen zwei Rateversuchen gegen ein fremdes Konto einmal erfolgreich anmelden und so nie gesperrt werden.
    /// </summary>
    public async Task RegisterSuccessAsync(IPAddress? remote, IPAddress? local, CancellationToken ct = default)
    {
        if (IsExempt(remote, local))
            return;

        var key = ClientAddress.PartitionKey(remote);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.IpBlocks.SingleOrDefaultAsync(b => b.Address == key && !b.IsPermanent, ct);
            if (row is null)
                return;

            row.Failures = Math.Max(0, row.Failures - 1);
            if (row.Failures == 0 && ToStatus(row, Now()) is { IsBlocked: false })
            {
                db.IpBlocks.Remove(row);
                _active.TryRemove(key, out _);
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Die Zeile wurde inzwischen vom Konsolenbefehl gelöscht: Das Ziel (kein Zähler mehr) ist erreicht.
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<IpBlock>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.IpBlocks.AsNoTracking()
            .OrderByDescending(b => b.IsPermanent).ThenByDescending(b => b.LastFailureUtc)
            .Take(MaxListed).ToListAsync(ct);
    }

    /// <summary>Entsperrt eine Adresse (IPv4-Adresse, IPv6-Adresse oder der in der Liste stehende Schlüssel) und löscht ihren Zähler.</summary>
    /// <param name="actorName">Wer entsperrt (Admin oder „Konsole“); steht im Protokoll.</param>
    public async Task<bool> UnblockAsync(string address, string? actorName = null, CancellationToken ct = default)
    {
        var key = NormalizeKey(address);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var removed = await db.IpBlocks.Where(b => b.Address == key).ExecuteDeleteAsync(ct);
            SetCached(key, IpBlockStatus.Open);
            if (removed > 0)
            {
                logger.LogWarning("IpUnblocked ip={Address}", key);
                if (audit is not null)
                    await audit.WriteAsync(AuditActions.IpUnblocked, null, actorName, key, ct: ct);
            }
            return removed > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> UnblockAllAsync(string? actorName = null, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var removed = await db.IpBlocks.ExecuteDeleteAsync(ct);
            _active.Clear();
            if (removed > 0)
            {
                logger.LogWarning("IpUnblockedAll count={Count}", removed);
                if (audit is not null)
                    await audit.WriteAsync(AuditActions.IpUnblockedAll, null, actorName, detail: $"{removed} Adressen", ct: ct);
            }
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static string NormalizeKey(string address)
    {
        var trimmed = address.Trim();
        return IPAddress.TryParse(trimmed, out var ip) ? ClientAddress.PartitionKey(ip) : trimmed;
    }

    private static void ApplyStage(IpBlock row, DateTime now)
    {
        var stage = row.Failures / FailuresPerStage;
        if (stage <= Stages.Count)
            row.BlockedUntilUtc = now + Stages[stage - 1];
        else
            row.IsPermanent = true;
    }

    private static IpBlockStatus ToStatus(IpBlock row, DateTime now) =>
        row.IsPermanent ? new IpBlockStatus(true, null, true)
        : row.BlockedUntilUtc > now ? new IpBlockStatus(true, row.BlockedUntilUtc, false)
        : IpBlockStatus.Open;

    private static bool IsActive(IpBlockStatus status, DateTime now) => status.IsPermanent || status.UntilUtc > now;

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private void SetCached(string key, IpBlockStatus status)
    {
        if (status.IsBlocked)
            _active[key] = status;
        else
            _active.TryRemove(key, out _);
    }

    private async Task RefreshIfStaleAsync(CancellationToken ct)
    {
        if (!IsStale())
            return;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!IsStale())
                return;

            var now = Now();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.IpBlocks.AsNoTracking().Where(b => b.IsPermanent || b.BlockedUntilUtc > now).ToListAsync(ct);
            var fresh = rows.ToDictionary(b => b.Address, b => ToStatus(b, now));
            foreach (var stale in _active.Keys.Where(key => !fresh.ContainsKey(key)))
                _active.TryRemove(stale, out _);
            foreach (var (key, status) in fresh)
                _active[key] = status;
            _loadedAtUtc = now;
            await TryCleanUpAsync(db, now, ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // Ohne Aufräumen wüchse die Tabelle bei vielen Quelladressen ohne Grenze. Abgelaufene Zähler setzt die Abklingzeit ohnehin zurück.
    // Ein Fehler beim Aufräumen darf nicht die Prüfung der gerade laufenden Anfrage abbrechen; der nächste Lauf versucht es wieder.
    private async Task TryCleanUpAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        try
        {
            await CleanUpIfDueAsync(db, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Die Sperrliste konnte nicht aufgeräumt werden.");
        }
    }

    private async Task CleanUpIfDueAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        if (now - _lastCleanupUtc < TimeSpan.FromHours(1))
            return;

        _lastCleanupUtc = now;
        var oldest = now - Decay;
        await db.IpBlocks.Where(b => !b.IsPermanent && b.LastFailureUtc < oldest).ExecuteDeleteAsync(ct);
    }

    private bool IsStale() => _loadedAtUtc == default || Now() - _loadedAtUtc >= CacheLifetime;
}
