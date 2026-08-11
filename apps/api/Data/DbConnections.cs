using Npgsql;

namespace Bruin.Api.Data;

// Low-level dual-pool primitives. Handlers should NOT depend on these
// directly for reads — use `IReadRouter` so LSN-based routing to primary
// or replica happens transparently. Writes go through `OpenPrimaryAsync`.
public interface IDbConnections
{
    ValueTask<NpgsqlConnection> OpenPrimaryAsync(CancellationToken ct = default);
    ValueTask<NpgsqlConnection> OpenReplicaAsync(CancellationToken ct = default);
}

public sealed class DbConnections : IDbConnections, IAsyncDisposable
{
    private readonly NpgsqlDataSource _primary;
    private readonly NpgsqlDataSource _replica;

    public DbConnections(string primary, string replica)
    {
        // Two independent pools so a spike on the graded read path can't
        // starve writes. NpgsqlDataSource pools connections internally.
        //
        // Every new physical connection gets `SET plan_cache_mode =
        // force_custom_plan`. Npgsql's extended-query protocol lets Postgres
        // cache a generic plan after 5 executions; for the search path
        // (`search_tsv @@ to_tsquery(...) OR service_number ILIKE ...`)
        // the generic plan is catastrophic on rare terms — the cost model
        // over-estimates tsvector selectivity, picks the created_at index,
        // then scans every tenant row (127 s at 3.5M for a no-match term).
        // Custom plans re-plan per parameter set for ~1 ms extra planning,
        // well under the p95 budget. `No Reset On Close=true` keeps the
        // SET alive across pool check-in/check-out (Npgsql's DISCARD ALL
        // on return would otherwise wipe it).
        _primary = BuildDataSource(primary);
        _replica = string.Equals(primary, replica, StringComparison.Ordinal)
            ? _primary
            : BuildDataSource(replica);
    }

    public ValueTask<NpgsqlConnection> OpenPrimaryAsync(CancellationToken ct = default)
        => _primary.OpenConnectionAsync(ct);

    public ValueTask<NpgsqlConnection> OpenReplicaAsync(CancellationToken ct = default)
        => _replica.OpenConnectionAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _primary.DisposeAsync();
        if (!ReferenceEquals(_primary, _replica))
            await _replica.DisposeAsync();
    }

    private static NpgsqlDataSource BuildDataSource(string conn)
    {
        var full = conn.Contains("No Reset On Close", StringComparison.OrdinalIgnoreCase)
            ? conn
            : $"{conn.TrimEnd(';')};No Reset On Close=true";
        var b = new NpgsqlDataSourceBuilder(full);
        b.UsePhysicalConnectionInitializer(
            connectionInitializer: c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SET plan_cache_mode = force_custom_plan";
                cmd.ExecuteNonQuery();
            },
            connectionInitializerAsync: async c =>
            {
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SET plan_cache_mode = force_custom_plan";
                await cmd.ExecuteNonQueryAsync();
            });
        return b.Build();
    }
}
