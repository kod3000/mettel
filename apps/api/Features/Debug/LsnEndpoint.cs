using Bruin.Api.Contracts;
using Bruin.Api.Data;
using Dapper;

namespace Bruin.Api.Features.Debug;

// GET /api/v1/debug/lsn — small observability endpoint powering the LSN
// status bar in the SPA. Reports:
//   - primary       — current WAL LSN on the primary
//   - replica       — last-replayed LSN on the streaming standby
//   - lagBytes      — bytes(primary) - bytes(replica)
//   - lagSeconds    — timestamp gap (aged forever on idle systems; use bytes
//                     for a truthful lag reading)
//   - reachable     — false when the replica connect failed
//
// Cheap enough to poll every 2 s from the SPA: two scalar SELECTs, no cache
// on the endpoint (ReplicaState's 250 ms cache already smooths the bursty
// case where the endpoint gets hit alongside a real read).
public static class LsnEndpoint
{
    public static void MapDebugLsn(this IEndpointRouteBuilder r)
    {
        r.MapGet("/api/v1/debug/lsn", GetLsnAsync)
            .WithName("GetDebugLsn")
            .ProducesProblem(401);
    }

    private static async Task<IResult> GetLsnAsync(
        IDbConnections db, CancellationToken ct)
    {
        string? primary = null, replica = null;
        double? lagSec = null;
        bool reachable = true;

        try
        {
            await using var primaryConn = await db.OpenPrimaryAsync(ct);
            primary = await primaryConn.ExecuteScalarAsync<string>(
                "SELECT pg_current_wal_lsn()::text");
        }
        catch { primary = null; }

        try
        {
            await using var replicaConn = await db.OpenReplicaAsync(ct);
            var row = await replicaConn.QuerySingleAsync<(string? lsn, double lagSec)>(@"
                SELECT
                    pg_last_wal_replay_lsn()::text                                                AS lsn,
                    GREATEST(0, EXTRACT(EPOCH FROM (now() - COALESCE(
                        pg_last_xact_replay_timestamp(), now()))))::float8                        AS lagSec");
            replica = row.lsn;
            lagSec = row.lagSec;
        }
        catch
        {
            reachable = false;
        }

        long lagBytes = 0;
        if (primary is not null && replica is not null)
        {
            try
            {
                var a = Bruin.Api.Domain.LsnCompare.Parse(primary);
                var b = Bruin.Api.Domain.LsnCompare.Parse(replica);
                lagBytes = Math.Max(0, (long)(a - b));
            }
            catch { /* leave zero */ }
        }

        return Results.Ok(new DebugLsnResponse(
            Primary: primary,
            Replica: replica,
            LagBytes: lagBytes,
            LagSeconds: lagSec ?? 0,
            Reachable: reachable));
    }
}
