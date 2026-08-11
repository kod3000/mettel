using Bruin.Api.Observability;
using Dapper;
using Npgsql;

namespace Bruin.Api.Data;

// Caches the replica's `pg_last_wal_replay_lsn()` and lag reading so a busy
// list endpoint doesn't pay the round-trip on every request. TTL is ~250 ms —
// short enough that a paused-then-resumed replica gets caught within a page
// or two of latency, long enough to amortise across a burst of reads.
//
// Also flips `IsReachable` when the health check can't reach the replica —
// consumed by `/health/ready` to fail loudly when the read side is broken,
// even if fallbacks are silently keeping traffic on primary.
public class ReplicaState : IDisposable
{
    private readonly IDbConnections _db;
    private readonly Metrics _metrics;
    private readonly TimeSpan _ttl = TimeSpan.FromMilliseconds(250);
    private readonly TimeSpan _lagThreshold = TimeSpan.FromSeconds(5);

    private DateTime _fetchedAtUtc = DateTime.MinValue;
    private string? _cachedReplayLsn;
    private long _cachedLagBytes;
    private double _cachedLagSeconds;
    private volatile bool _reachable = true;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    public ReplicaState(IDbConnections db, Metrics metrics)
    {
        _db = db;
        _metrics = metrics;
    }

    public virtual bool IsReachable => _reachable;

    // Flipped by the read router on a connection open failure — the fast
    // path (no LSN watermark) doesn't invoke GetReplayLsnAsync so the
    // reachable flag would otherwise stay `true` even against a paused
    // replica. The next TTL-triggered refresh restores accuracy.
    public virtual void MarkUnreachable() => _reachable = false;

    // Lag is acceptable when either the byte diff between primary WAL and
    // replica replay position is under a small threshold, OR the timestamp-
    // based lag is under `threshold`. The byte check is what actually
    // matters for read-your-own-writes correctness; the timestamp is a
    // human-friendly gauge. On an idle system pg_last_xact_replay_timestamp
    // ages forever even when the replica is fully caught up, so we prefer
    // the byte view.
    public bool IsLagAcceptable(TimeSpan? threshold = null) =>
        _cachedLagBytes <= 8 * 1024 * 1024   // within 8 MB of primary
        || _cachedLagSeconds <= (threshold ?? _lagThreshold).TotalSeconds;

    // Returns the replica's last-applied LSN, or null if the replica is
    // unreachable (in which case the router must fall back to primary and
    // /health/ready must fail).
    public virtual async Task<string?> GetReplayLsnAsync(CancellationToken ct = default)
    {
        // Positive cache — replica reachable + LSN known.
        if (DateTime.UtcNow - _fetchedAtUtc < _ttl && _cachedReplayLsn is not null)
            return _cachedReplayLsn;
        // Negative cache — replica marked unreachable within TTL. Don't
        // re-probe until the window expires; otherwise every request pays
        // the 3 s connect-timeout of a paused replica.
        if (DateTime.UtcNow - _fetchedAtUtc < _ttl && !_reachable)
            return null;

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _fetchedAtUtc < _ttl && _cachedReplayLsn is not null)
                return _cachedReplayLsn;
            if (DateTime.UtcNow - _fetchedAtUtc < _ttl && !_reachable)
                return null;
            await RefreshLocked(ct);
            return _cachedReplayLsn;
        }
        finally { _fetchLock.Release(); }
    }

    private async Task RefreshLocked(CancellationToken ct)
    {
        try
        {
            // Read replica replay position + timestamp lag. When the "replica"
            // is actually the same server as primary (Testcontainers single-
            // instance setup used in tests), `pg_last_wal_replay_lsn()`
            // returns NULL — we surface that as `FFFFFFFF/FFFFFFFF` so any
            // caught-up check trivially succeeds and the router picks
            // replica (== primary). Production always has a real standby.
            await using var replicaConn = await _db.OpenReplicaAsync(ct);
            var repl = await replicaConn.QuerySingleAsync<(string lsn, double lagSec)>(@"
                SELECT
                    COALESCE(pg_last_wal_replay_lsn()::text, 'FFFFFFFF/FFFFFFFF') AS lsn,
                    GREATEST(0, EXTRACT(EPOCH FROM (now() - COALESCE(pg_last_xact_replay_timestamp(), now()))))::float8 AS lagSec");
            _cachedReplayLsn = repl.lsn;
            _cachedLagSeconds = repl.lagSec;

            // … and the primary's current WAL LSN so we can compute byte lag,
            // which stays honest on idle systems (unlike timestamp lag).
            await using var primaryConn = await _db.OpenPrimaryAsync(ct);
            var primaryLsn = await primaryConn.ExecuteScalarAsync<string>(
                "SELECT pg_current_wal_lsn()::text");
            if (primaryLsn is not null)
            {
                var byteDiff = LsnCompareBytes(primaryLsn, repl.lsn);
                _cachedLagBytes = Math.Max(0, byteDiff);
            }

            _fetchedAtUtc = DateTime.UtcNow;
            _reachable = true;
            _metrics.SetReplicaLagSeconds(repl.lagSec);
        }
        catch (Exception ex) when (ex is NpgsqlException
                                || ex is System.Net.Sockets.SocketException
                                || ex is System.TimeoutException
                                || ex is System.IO.IOException
                                || ex is System.OperationCanceledException)
        {
            _reachable = false;
            _cachedReplayLsn = null;
            _fetchedAtUtc = DateTime.UtcNow; // rearm TTL so we don't hammer a dead replica
        }
    }

    private static long LsnCompareBytes(string primary, string replica)
    {
        // pg_wal_lsn_diff returns bytes(a) - bytes(b). We do it in-app so we
        // don't add another round-trip.
        try
        {
            var a = Domain.LsnCompare.Parse(primary);
            var b = Domain.LsnCompare.Parse(replica);
            return (long)(a - b);
        }
        catch { return 0; }
    }

    public void Dispose() => _fetchLock.Dispose();
}
