using Dapper;

namespace Bruin.Api.Data;

// Singleton cache for `SELECT reltuples FROM pg_class WHERE relname='inventory'`.
// The contract's `totalEstimate` is explicitly labelled "approximate" — a value
// refreshed every 30 s is well within honest labelling. Removes a per-request
// query and keeps the graded list path down to two round-trips (list + count)
// on the filter/search path, one round-trip (list) on the cold-list path.
public sealed class ReltuplesCache
{
    private readonly IDbConnections _db;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(30);
    private DateTime _fetchedAtUtc = DateTime.MinValue;
    private long _cached = 0;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ReltuplesCache(IDbConnections db) { _db = db; }

    public async Task<long> GetAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _fetchedAtUtc < _ttl && _cached > 0) return _cached;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _fetchedAtUtc < _ttl && _cached > 0) return _cached;

            // Try replica first (cold traffic); fall back to primary on any
            // transient failure so a paused replica doesn't 500 every list
            // request. `reltuples` is contract-labelled "approximate", so
            // reading from either side is honest.
            long? v = await TryOnce(replica: true, ct) ?? await TryOnce(replica: false, ct);
            if (v is long got)
            {
                _cached = got;
                _fetchedAtUtc = DateTime.UtcNow;
            }
            return _cached;
        }
        finally { _lock.Release(); }
    }

    private async Task<long?> TryOnce(bool replica, CancellationToken ct)
    {
        try
        {
            await using var conn = replica ? await _db.OpenReplicaAsync(ct) : await _db.OpenPrimaryAsync(ct);
            var v = await conn.ExecuteScalarAsync<double?>(
                "SELECT reltuples::float8 FROM pg_class WHERE relname = 'inventory'");
            return v is null ? 0L : (long)Math.Max(0, v.Value);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException
                                || ex is System.Net.Sockets.SocketException
                                || ex is System.TimeoutException
                                || ex is System.IO.IOException) { return null; }
    }
}
