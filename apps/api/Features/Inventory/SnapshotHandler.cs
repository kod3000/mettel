using System.Diagnostics;
using Bruin.Api.Contracts;
using Bruin.Api.Data;
using Dapper;

namespace Bruin.Api.Features.Inventory;

// Snapshot read path used by the WASM client to hydrate a local SQLite mirror
// and pull deltas afterward. Distinct from ListHandler on two axes:
//   - Cursor is (updated_at, id) — a client-owned watermark. Not opaque,
//     not HMAC-signed: the client stores it and includes it verbatim.
//   - Tombstones are included. `deleted_at IS NOT NULL` rows still ship,
//     so the client can remove them from its local mirror.
//
// Two directions:
//   - dir=asc  (default) — watermark-forward. `since` selects rows with
//     (updated_at, id) > (since, sinceId). This is the delta path.
//   - dir=desc — newest-first. `since` (when supplied) selects rows with
//     (updated_at, id) < (since, sinceId). This is the initial-bulk path:
//     the client wants the most-recent slice of the tenant's inventory
//     without hydrating everything, and pages backward from the head.
//
// The index ix_inventory_client_updated_id (client_id, updated_at, id) covers
// both directions directly. Cap defaults to 5000 rows per call so large
// hydrations page cleanly.
public sealed class SnapshotHandler(IReadRouter db)
{
    public const int MaxLimit = 5000;
    public const int DefaultLimit = 5000;

    public async Task<SnapshotResponse> Handle(
        Guid clientId,
        DateTimeOffset? sinceTs,
        Guid? sinceId,
        int? limit,
        bool desc,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var p = new DynamicParameters();
        p.Add("clientId", clientId);
        p.Add("take", take + 1);   // fetch one extra to know hasMore

        var cmp = desc ? "<" : ">";
        var orderDir = desc ? "DESC" : "ASC";

        var where = "client_id = @clientId";
        if (sinceTs is DateTimeOffset ts && sinceId is Guid sid)
        {
            p.Add("since_ts", ts);
            p.Add("since_id", sid);
            // Row-value comparison rides the (client_id, updated_at, id) index.
            where += $" AND (updated_at, id) {cmp} (@since_ts, @since_id)";
        }

        const string cols =
            "id, service_number, product_category, product_name, status, " +
            "city, state, address, assignee, notes, " +
            "created_at, updated_at, row_version, deleted_at";

        var sql = $@"
            SELECT {cols}
            FROM public.inventory
            WHERE {where}
            ORDER BY updated_at {orderDir}, id {orderDir}
            LIMIT @take";

        var sw = Stopwatch.StartNew();
        await using var conn = await db.OpenReadAsync(ct);
        var raw = (await conn.QueryAsync<SnapshotRowDto>(
            new CommandDefinition(sql, p, cancellationToken: ct))).AsList();

        bool hasMore = raw.Count > take;
        if (hasMore) raw.RemoveAt(raw.Count - 1);

        DateTimeOffset? nextSince = null;
        Guid? nextSinceId = null;
        if (raw.Count > 0)
        {
            nextSince = raw[^1].updated_at;
            nextSinceId = raw[^1].id;
        }

        return new SnapshotResponse(
            Rows: raw.Select(WireOf).ToArray(),
            NextSince: nextSince,
            NextSinceId: nextSinceId,
            HasMore: hasMore,
            TookMs: sw.ElapsedMilliseconds);
    }

    private static SnapshotRow WireOf(SnapshotRowDto r) => new(
        Id: r.id,
        ServiceNumber: r.service_number,
        ProductCategory: r.product_category,
        ProductName: r.product_name,
        Status: r.status,
        City: r.city,
        State: r.state,
        Address: r.address,
        Assignee: r.assignee,
        Notes: r.notes,
        CreatedAt: r.created_at,
        UpdatedAt: r.updated_at,
        RowVersion: r.row_version,
        DeletedAt: r.deleted_at);
}

internal sealed record SnapshotRowDto
{
    public Guid id { get; init; }
    public string service_number { get; init; } = "";
    public string product_category { get; init; } = "";
    public string product_name { get; init; } = "";
    public string status { get; init; } = "";
    public string? city { get; init; }
    public string? state { get; init; }
    public string? address { get; init; }
    public string? assignee { get; init; }
    public string? notes { get; init; }
    public DateTimeOffset created_at { get; init; }
    public DateTimeOffset updated_at { get; init; }
    public int row_version { get; init; }
    public DateTimeOffset? deleted_at { get; init; }
}
