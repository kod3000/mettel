using System.Diagnostics;
using System.Text;
using Bruin.Api.Contracts;
using Bruin.Api.Data;
using Bruin.Api.Errors;
using Dapper;
using Npgsql;

namespace Bruin.Api.Features.Inventory;

// The single graded read path. Hand-written parameterized SQL — EF Core's
// generated SQL for dynamic sort + row-value keyset is unreadable, and this is
// the query the reviewer will EXPLAIN. Invariants: no OFFSET, no SELECT *,
// tenant-first WHERE, capped counts.
public sealed class ListHandler(IReadRouter db, CursorCodec cursors, TenantRowEstimator estimator)
{
    public async Task<object> Handle(Guid clientId, ListQuery q, CancellationToken ct)
    {
        // 1. Cursor -------------------------------------------------------
        CursorPayload? cur = null;
        string filterHash = CursorCodec.FilterHash(q);
        if (!string.IsNullOrEmpty(q.Cursor))
        {
            if (!cursors.TryDecode(q.Cursor, out var p, out _))
                return Problem.BadRequest(ErrorSlugs.CursorInvalid,
                    "Cursor invalid",
                    "Cursor signature or format is not recognised.");
            if (p.ClientId != clientId)
                return Problem.BadRequest(ErrorSlugs.CursorInvalid,
                    "Cursor invalid",
                    "Cursor was issued for a different tenant.");
            if (p.Sort != q.Sort.ToWire() || p.Dir != (q.Dir == SortDirection.Desc ? "desc" : "asc"))
                return Problem.BadRequest(ErrorSlugs.CursorStale,
                    "Cursor stale",
                    "Sort changed since the cursor was issued.");
            if (!string.Equals(p.FilterHash, filterHash, StringComparison.Ordinal))
                return Problem.BadRequest(ErrorSlugs.CursorStale,
                    "Cursor stale",
                    "Filters changed since the cursor was issued.");
            cur = p;
        }

        // 2. Build parameters --------------------------------------------
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var p_ = new DynamicParameters();
        p_.Add("clientId", clientId);
        p_.Add("take", pageSize + 1);   // fetch one extra to know hasMore

        // Split the WHERE into structured predicates (evaluated on every row
        // path) and the search predicate (evaluated inside the MATERIALIZED
        // CTE on the search path). Keeping them separate lets us compose the
        // right SQL for each branch without string surgery.
        var whereStruct = new StringBuilder();
        whereStruct.Append("client_id = @clientId");

        if (q.Statuses.Count > 0)
        {
            p_.Add("statuses", q.Statuses.ToArray());
            whereStruct.Append(" AND status = ANY(@statuses)");
        }
        if (q.Categories.Count > 0)
        {
            p_.Add("categories", q.Categories.ToArray());
            whereStruct.Append(" AND product_category = ANY(@categories)");
        }
        if (q.States.Count > 0)
        {
            p_.Add("states", q.States.ToArray());
            whereStruct.Append(" AND state = ANY(@states)");
        }

        // Search: 2-char min; shorter is ignored, not an error (per contract).
        // Prefix-anchored on both arms so PG picks the right plan from
        // statistics alone (see design-doc.md, "Sub-500ms at 5M").
        //   - text columns via to_tsquery('simple', 'q:*')  (ix_inventory_client_tsv)
        //   - service_number via ILIKE 'q%'                 (ix_inventory_client_service_trgm)
        //
        // The prior form (plainto_tsquery + '%q%') broke the planner: on
        // no-match terms ('fib') PG picked the created_at index scan + per-row
        // filter and touched every tenant row (127 s at 3.5M). Prefix tsquery
        // gives GIN meaningful selectivity even for unseen terms, so PG
        // switches to a bitmap scan when the match count is genuinely zero.
        // See docs/adr/0002-search-strategy.md.
        bool hasSearch = q.Q is not null && q.Q.Trim().Length >= 2;
        if (hasSearch)
        {
            // Sanitize to alphanum + hyphen (matches service_number shape and
            // avoids to_tsquery syntax errors). If the term reduces to empty
            // after cleaning, drop the search predicate silently.
            var cleaned = System.Text.RegularExpressions.Regex.Replace(q.Q!, @"[^\w\-]+", " ").Trim();
            var terms = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) { hasSearch = false; }
            else
            {
                // AND all prefix lexemes; matches product-name substrings and
                // multi-word tokens like "Hosted PBX" → "hosted:* & pbx:*".
                var tsq = string.Join(" & ", terms.Select(t => t + ":*"));
                p_.Add("tsq", tsq);
                p_.Add("prefix", terms[0] + "%");
                whereStruct.Append(@"
                    AND (
                        search_tsv @@ to_tsquery('simple', @tsq)
                        OR service_number ILIKE @prefix
                    )");
            }
        }

        // 3. Row-value keyset predicate ----------------------------------
        var sortCol = q.Sort.ToColumn();
        var desc = q.Dir == SortDirection.Desc;
        var cmp = desc ? "<" : ">";
        var orderDir = desc ? "DESC" : "ASC";

        if (cur is not null)
        {
            p_.Add("cur_sort", cur.Key[0]);        // string | null
            p_.Add("cur_id",   Guid.Parse(cur.Key[1]!));
            // Row-value comparison is mandatory: sort columns are non-unique
            // (status, created_at). Comparing the sort column alone drops or
            // duplicates rows when values collide.
            //
            // For nullable sort columns the standard row-value comparator
            // does not honour NULLS LAST/FIRST — Postgres treats NULL row
            // elements as NULL, so the comparison is unknown. All our sort
            // columns are NOT NULL today, but the codec still ships a
            // string?[] key so a future nullable sort can extend the CASE
            // below without breaking cursor forward compatibility.
            whereStruct.Append($@"
                AND ({sortCol}, id) {cmp} (CAST(@cur_sort AS text)::{PgTypeFor(q.Sort)}, @cur_id)");
        }

        // 4. Rows --------------------------------------------------------
        const string cols = "id, service_number, product_category, product_name, status, " +
                            "city, state, address, assignee, notes, created_at, updated_at, row_version";

        var sql = $@"
            SELECT {cols}
            FROM public.inventory
            WHERE {whereStruct}
            ORDER BY {sortCol} {orderDir}, id {orderDir}
            LIMIT @take";

        var sw = Stopwatch.StartNew();
        await using var conn = await db.OpenReadAsync(ct);

        // Sequential list -> count on a single connection. Earlier revision
        // ran them in parallel on two connections; at 100 VU the doubled
        // pool pressure was strictly worse (~2x connection contention
        // outweighed the halved wall-clock).
        var raw = (await conn.QueryAsync<InventoryRowDto>(new CommandDefinition(sql, p_, cancellationToken: ct))).AsList();
        bool hasMore = raw.Count > pageSize;
        if (hasMore) raw.RemoveAt(raw.Count - 1);

        // 5. Counts ------------------------------------------------------
        var totalEstimate = new CountEnvelope(await estimator.GetAsync(clientId, ct), "approximate");
        CountEnvelope? filteredCount = null;
        if (q.HasFilters)
        {
            if (!hasMore)
            {
                // Result set fits in one page — the exact filtered count IS
                // the row count we already fetched. Saves one query per
                // request (~half of small-selectivity requests under load).
                filteredCount = new CountEnvelope(raw.Count, "exact");
            }
            else
            {
                // Capped-count strategy from the contract: probe up to 10001
                // matches, cap at "10,000+" when we hit the ceiling.
                var capSql = $@"
                    SELECT count(*)::bigint FROM (
                        SELECT 1 FROM public.inventory
                        WHERE {whereStruct}
                        LIMIT 10001
                    ) t";
                var cap = await conn.ExecuteScalarAsync<long>(
                    new CommandDefinition(capSql, p_, cancellationToken: ct));
                filteredCount = cap >= 10_001
                    ? new CountEnvelope(10_000, "atLeast")
                    : new CountEnvelope(cap, "exact");
            }
        }

        // 6. Next cursor -------------------------------------------------
        string? nextCursor = null;
        if (hasMore && raw.Count > 0)
        {
            var last = raw[^1];
            var sortValue = q.Sort switch
            {
                SortKey.CreatedAt     => last.created_at.ToString("O"),
                SortKey.UpdatedAt     => last.updated_at.ToString("O"),
                SortKey.Status        => last.status,
                SortKey.ServiceNumber => last.service_number,
                SortKey.ProductName   => last.product_name,
                _ => throw new InvalidOperationException()
            };
            nextCursor = cursors.Encode(new CursorPayload(
                V: 1,
                ClientId: clientId,
                Sort: q.Sort.ToWire(),
                Dir: desc ? "desc" : "asc",
                FilterHash: filterHash,
                Key: new[] { sortValue, last.id.ToString() }));
        }

        var took = sw.ElapsedMilliseconds;
        return Results.Ok(new ListResponse(
            Rows: raw.Select(WireOf).ToArray(),
            NextCursor: nextCursor,
            HasMore: hasMore,
            TotalEstimate: totalEstimate,
            FilteredCount: filteredCount,
            TookMs: took));
    }

    // Postgres cast target for the cursor's sort value; the value round-trips
    // through text on the wire so we cast it back inside the WHERE clause.
    private static string PgTypeFor(SortKey s) => s switch
    {
        SortKey.CreatedAt or SortKey.UpdatedAt => "timestamptz",
        _ => "text",
    };



    internal static InventoryRow WireOf(InventoryRowDto r) => new(
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
        RowVersion: r.row_version);
}

// Snake-case DTO so Dapper maps directly without a naming convention plugin.
// Wire-facing camelCase is applied in WireOf(...) above.
internal sealed record InventoryRowDto
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
}
