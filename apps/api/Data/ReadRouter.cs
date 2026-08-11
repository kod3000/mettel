using Bruin.Api.Domain;
using Bruin.Api.Observability;
using Npgsql;

namespace Bruin.Api.Data;

// Scoped shim over IDbConnections that implements the read-your-own-writes
// protocol from docs/API_CONTRACT.md:
//
//   - X-Min-LSN absent               → replica (fast path)
//   - X-Min-LSN present, replica ≥   → replica
//   - X-Min-LSN present, replica <   → primary (fallback, +metric)
//   - Replica unreachable            → primary (fallback, +metric)
//
// The tenant read handler consumes this instead of touching IDbConnections
// directly.
public interface IReadRouter
{
    ValueTask<NpgsqlConnection> OpenReadAsync(CancellationToken ct = default);

    // Explicit escape hatch for bulk-job status reads (contract exception:
    // low volume, must never be stale, hits primary intentionally). Callers
    // opting out are counted separately so the metric stays honest.
    ValueTask<NpgsqlConnection> OpenPrimaryAsync(CancellationToken ct = default);
}

public sealed class ReadRouter(
    IDbConnections db,
    ILsnContext lsn,
    ReplicaState replica,
    Metrics metrics) : IReadRouter
{
    public async ValueTask<NpgsqlConnection> OpenReadAsync(CancellationToken ct = default)
    {
        // Even the no-LSN fast path calls GetReplayLsnAsync for its side
        // effect of refreshing `IsReachable` (and lag stats). The state
        // cache has a 250 ms TTL, so a 100 VU burst probes the replica
        // exactly once per window — negligible cost, and it catches a
        // dead replica in ≤ 250 ms instead of hanging the first requesters
        // until Npgsql's per-connection timeout expires.
        var replayLsn = await replica.GetReplayLsnAsync(ct);

        if (lsn.MinLsn is null)
        {
            if (replica.IsReachable)
            {
                try { return await db.OpenReplicaAsync(ct); }
                catch (NpgsqlException) { replica.MarkUnreachable(); }
                catch (System.Net.Sockets.SocketException) { replica.MarkUnreachable(); }
                catch (System.TimeoutException) { replica.MarkUnreachable(); }
            }
            metrics.IncReadPrimaryFallback();
            return await db.OpenPrimaryAsync(ct);
        }

        if (replayLsn is not null && LsnCompare.Caught(replayLsn, lsn.MinLsn))
        {
            try { return await db.OpenReplicaAsync(ct); }
            catch (Exception ex) when (ex is NpgsqlException
                                    || ex is System.Net.Sockets.SocketException
                                    || ex is System.TimeoutException
                                    || ex is System.IO.IOException)
            {
                replica.MarkUnreachable();
            }
        }

        metrics.IncReadPrimaryFallback();
        return await db.OpenPrimaryAsync(ct);
    }

    public ValueTask<NpgsqlConnection> OpenPrimaryAsync(CancellationToken ct = default)
        => db.OpenPrimaryAsync(ct);
}
