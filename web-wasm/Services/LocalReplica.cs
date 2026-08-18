using System.Text;
using System.Text.Json;
using Bruin.Web.Wasm.Models;
using Microsoft.JSInterop;

namespace Bruin.Web.Wasm.Services;

// Per-tenant local SQLite mirror of the inventory table.
//
// Purpose: sub-ms filter / sort / search over hydrated rows, so the grid
// answers reads without a network round-trip. Server stays the source of
// truth for writes; local is a read cache with an upsert path.
//
// State machine:
//   Idle -> HydrateAsync(clientId) -> Hydrating -> Ready
//   On tenant switch, caller invokes CloseAsync then reopens for the new
//   client so each tenant lives in its own OPFS-backed DB file.
//
// JS boundary: everything crosses through window.bruinDb (see
// wwwroot/js/sqlite-interop.js). Bulk paths ship one JSON blob per batch;
// query results come back as JSON so we round-trip once per call instead
// of once per row.
public sealed class LocalReplica
{
    private readonly IJSRuntime _js;
    private readonly BruinApiClient _api;
    private readonly ErrorReporter _errors;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Open DB descriptor + watermarks. Rebuilt per tenant.
    //
    // The mirror is a "recent slice" cache — we hold at most MaxRows and
    // keep the most-recently-updated ones. Two watermarks track the slice:
    //   - _latestWatermark: the *newest* (updated_at, id) we have locally.
    //     Delta pulls advance forward from here (dir=asc, since=latest).
    //   - _oldestWatermark: the *oldest* (updated_at, id) we have locally.
    //     Backfill pulls extend the slice backward (dir=desc, since=oldest)
    //     until MaxRows is hit. Not surfaced today; wired for future use.
    private Guid? _clientId;
    private string? _dbName;
    private DateTimeOffset? _latestWatermark;
    private string? _latestWatermarkId;
    private DateTimeOffset? _oldestWatermark;
    private string? _oldestWatermarkId;
    private long _rowCount;
    private DateTimeOffset? _lastSyncAt;

    // Cap the local slice. In-memory SQLite (OPFS unavailable) tops out
    // around 500 MB of WASM heap; ~100k rows × ~400 B leaves headroom for
    // indexes, JS marshalling churn, and Blazor GC. Callers can raise this
    // when OPFS SAH-pool is confirmed active (persists to disk instead).
    public const int DefaultMaxRows = 100_000;

    // Optional server-total hint, set by the grid off its most recent
    // ListResponse.totalEstimate. Powers the "N of M · recent" chip label
    // so the operator sees the slice size against the true tenant total.
    // Null until the grid has heard from the server at least once.
    public long? ServerRowsHint { get; private set; }
    public void SetServerRowsHint(long value)
    {
        if (ServerRowsHint == value) return;
        ServerRowsHint = value;
        Changed?.Invoke();
    }

    public LocalReplica(IJSRuntime js, BruinApiClient api, ErrorReporter errors)
    {
        _js = js;
        _api = api;
        _errors = errors;
    }

    public bool IsOpen => _dbName is not null;
    public bool IsReady => IsOpen && _rowCount > 0;
    public long RowCount => _rowCount;
    public DateTimeOffset? LastSyncAt => _lastSyncAt;
    public Guid? ClientId => _clientId;

    // Signal for the UI. Fires on hydration start/progress/complete and
    // on write-through upserts so the grid can re-query.
    public event Action? Changed;

    // ---- Lifecycle ------------------------------------------------------

    public async Task OpenAsync(Guid clientId, CancellationToken ct = default)
    {
        if (_clientId == clientId && IsOpen) return;
        await CloseAsync();

        _clientId = clientId;
        // Namespacing per tenant keeps a bad row from one client from
        // polluting another's mirror, and lets us wipe a single tenant.
        _dbName = $"bruin-{clientId:N}.db";

        await _js.InvokeAsync<JsonElement>("bruinDb.open", ct, _dbName);
        await _js.InvokeVoidAsync("bruinDb.exec", ct, _dbName, EnsureSchemaSql);

        // Restore counters from what's already on disk. First open of a
        // fresh tenant lands here with rowCount=0 and null watermarks.
        _rowCount = await ScalarLongAsync("SELECT COUNT(*) FROM inventory", ct);
        (_latestWatermark, _latestWatermarkId) = await LoadWatermarkAsync(desc: true, ct);
        (_oldestWatermark, _oldestWatermarkId) = await LoadWatermarkAsync(desc: false, ct);
        Changed?.Invoke();
    }

    // Read the (updated_at, id) pair at the head or tail of the local
    // slice. `desc=true` returns the newest row (latest watermark);
    // `desc=false` returns the oldest row (backfill anchor).
    private async Task<(DateTimeOffset?, string?)> LoadWatermarkAsync(
        bool desc, CancellationToken ct)
    {
        var order = desc ? "DESC" : "ASC";
        var raw = await ScalarStringAsync(
            $"SELECT updated_at || '|' || id FROM inventory " +
            $"ORDER BY updated_at {order}, id {order} LIMIT 1", ct);
        return ParseWatermark(raw);
    }

    public async Task CloseAsync()
    {
        // We don't close the underlying handle — SAH-pool keeps it open
        // for reuse across component re-renders. Clearing local state is
        // enough. `wipe` is the explicit "delete the file" path (below).
        _clientId = null;
        _dbName = null;
        _latestWatermark = null;
        _latestWatermarkId = null;
        _oldestWatermark = null;
        _oldestWatermarkId = null;
        _rowCount = 0;
        _lastSyncAt = null;
        Changed?.Invoke();
        await Task.CompletedTask;
    }

    public async Task WipeAsync(CancellationToken ct = default)
    {
        if (_dbName is null) return;
        var name = _dbName;
        await CloseAsync();
        await _js.InvokeVoidAsync("bruinDb.wipe", ct, name);
    }

    // ---- Hydration + delta sync ----------------------------------------

    // Bring the local mirror up-to-date with the server. Two phases:
    //
    //   1. DELTA (dir=asc, since=latest)   — always run first. Cheap when
    //      the mirror is already close to head; pulls anything newer than
    //      our latest watermark and advances it.
    //   2. BACKFILL (dir=desc, since=oldest) — extends the slice backward
    //      until either we hit MaxRows or the server signals HasMore=false.
    //      On an empty mirror this is the initial bulk pull; on a partial
    //      mirror it's a widen-the-window.
    //
    // Each batch's JSON payload is released between iterations so the JS
    // heap doesn't accumulate — a full ~100k hydrate then holds only the
    // SQLite pages + one in-flight batch worth of transient allocations.
    public async Task HydrateAsync(
        IProgress<HydrationProgress>? progress,
        int maxRows = DefaultMaxRows,
        CancellationToken ct = default)
    {
        if (_dbName is null) throw new InvalidOperationException("Call OpenAsync first.");

        int batches = 0;
        long total = 0;
        bool hasMore = false;
        long lastBatchMs = 0;

        // Phase 1: delta forward from the newest we already have. Skipped
        // when the mirror is empty (no watermark to advance from).
        if (_latestWatermark is not null)
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = await FetchSnapshotAsync(
                    since: _latestWatermark, sinceId: _latestWatermarkId,
                    dir: "asc", ct: ct);
                lastBatchMs = batch.TookMs;
                hasMore = batch.HasMore;

                if (batch.Rows.Count > 0)
                {
                    await UpsertBatchAsync(batch.Rows, ct);
                    _latestWatermark = batch.NextSince ?? _latestWatermark;
                    _latestWatermarkId = batch.NextSinceId ?? _latestWatermarkId;
                    total += batch.Rows.Count;
                    batches++;
                }
                await ReportProgressAsync(progress, batches, total, hasMore, lastBatchMs, ct);
                if (!batch.HasMore) break;
            }
        }

        // Phase 2: backfill from head-newest downward until we hit the
        // slice cap. On an empty mirror this uses no `since` and starts
        // at the very newest row; on a partial mirror we resume from the
        // oldest we already have to keep extending the window.
        while (!ct.IsCancellationRequested && _rowCount < maxRows)
        {
            var remaining = (int)Math.Min(5000, maxRows - _rowCount);
            var batch = await FetchSnapshotAsync(
                since: _oldestWatermark, sinceId: _oldestWatermarkId,
                dir: "desc", limit: remaining, ct: ct);
            lastBatchMs = batch.TookMs;
            hasMore = batch.HasMore;

            if (batch.Rows.Count > 0)
            {
                await UpsertBatchAsync(batch.Rows, ct);
                _oldestWatermark = batch.NextSince ?? _oldestWatermark;
                _oldestWatermarkId = batch.NextSinceId ?? _oldestWatermarkId;
                // The first batch of an empty mirror also seeds the "latest"
                // watermark — the DESC pull's first row IS the newest.
                if (_latestWatermark is null && batch.Rows.Count > 0)
                {
                    _latestWatermark = batch.Rows[0].UpdatedAt;
                    _latestWatermarkId = batch.Rows[0].Id;
                }
                total += batch.Rows.Count;
                batches++;
            }
            await ReportProgressAsync(progress, batches, total, hasMore, lastBatchMs, ct);
            if (!batch.HasMore) break;
        }
    }

    // Single-batch snapshot fetch with error routing. Kept separate so
    // both phases share cancellation + ApiException handling.
    private async Task<SnapshotResponse> FetchSnapshotAsync(
        DateTimeOffset? since, string? sinceId, string dir,
        int limit = 5000, CancellationToken ct = default)
    {
        try
        {
            return await _api.SnapshotAsync(since, sinceId, limit, dir, ct);
        }
        catch (ApiException ex)
        {
            _errors.Report(ex, "snapshot");
            throw;
        }
    }

    private async Task ReportProgressAsync(
        IProgress<HydrationProgress>? progress,
        int batches, long total, bool hasMore, long lastBatchMs,
        CancellationToken ct)
    {
        _lastSyncAt = DateTimeOffset.UtcNow;
        _rowCount = await ScalarLongAsync(
            "SELECT COUNT(*) FROM inventory WHERE deleted_at IS NULL", ct);
        progress?.Report(new HydrationProgress(
            Batches: batches,
            RowsThisRun: total,
            RowsInMirror: _rowCount,
            HasMore: hasMore,
            LastBatchMs: lastBatchMs));
        Changed?.Invoke();
    }

    // ---- Read path ------------------------------------------------------

    public sealed record LocalListParams(
        string? Q = null,
        IReadOnlyCollection<string>? Status = null,
        IReadOnlyCollection<string>? ProductCategory = null,
        IReadOnlyCollection<string>? State = null,
        string? Sort = null,
        string? Dir = null,
        int PageSize = 100,
        int Offset = 0);

    public sealed record LocalListResult(
        IReadOnlyList<InventoryRow> Rows,
        long FilteredCount,
        long TotalCount,
        long TookMs);

    // Local list. Mirrors ListHandler behaviour on the server, but:
    //   - Search is full substring (no prefix limit) — WASM's headline win.
    //   - No cursor: OFFSET/LIMIT is fine over an indexed SQLite table for
    //     the sizes we hold locally (up to a few hundred thousand rows).
    //   - Deleted rows filtered out.
    public async Task<LocalListResult> QueryListAsync(
        LocalListParams p, CancellationToken ct = default)
    {
        if (_dbName is null) throw new InvalidOperationException("Local replica not open.");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var (where, binds) = BuildWhere(p);
        var order = BuildOrderBy(p.Sort, p.Dir);
        var pageSize = Math.Clamp(p.PageSize, 1, 500);
        var offset = Math.Max(p.Offset, 0);

        // Two queries: filtered count (exact — cheap in SQLite over an
        // indexed local dataset) + the page itself. We could merge them
        // via a window function, but two round-trips through the JS
        // interop is still ~2ms — well below the network baseline.
        var countSql = $"SELECT COUNT(*) FROM inventory WHERE {where}";
        var filtered = await ScalarLongAsync(countSql, binds, ct);

        var pageSql = $@"
            SELECT id, service_number, product_category, product_name, status,
                   city, state, address, assignee, notes,
                   created_at, updated_at, row_version
            FROM inventory
            WHERE {where}
            ORDER BY {order}
            LIMIT {pageSize} OFFSET {offset}";
        var rows = await QueryRowsAsync(pageSql, binds, ct);

        return new LocalListResult(
            Rows: rows,
            FilteredCount: filtered,
            TotalCount: _rowCount,
            TookMs: sw.ElapsedMilliseconds);
    }

    // ---- Write-through --------------------------------------------------

    // Upsert a single row (called after a successful create/patch).
    public Task UpsertAsync(InventoryRow row, CancellationToken ct = default)
        => UpsertBatchAsync(new[] { ToSnapshotRow(row) }, ct);

    // Tombstone (called after a successful delete). We mark deleted_at
    // = now() rather than physically removing so a subsequent snapshot
    // sync converges (the server ships a deleted-at timestamp too).
    public async Task TombstoneAsync(string id, CancellationToken ct = default)
    {
        if (_dbName is null) return;
        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        await _js.InvokeVoidAsync(
            "bruinDb.exec", ct, _dbName,
            $"UPDATE inventory SET deleted_at = '{nowIso}' WHERE id = '{id.Replace("'", "''")}'");
        _rowCount = await ScalarLongAsync(
            "SELECT COUNT(*) FROM inventory WHERE deleted_at IS NULL", ct);
        Changed?.Invoke();
    }

    // ---- SQL construction ----------------------------------------------

    private static (string Where, object[] Binds) BuildWhere(LocalListParams p)
    {
        var sb = new StringBuilder("deleted_at IS NULL");
        var binds = new List<object>();

        if (p.Status is { Count: > 0 })
            sb.Append(" AND status IN (").Append(Placeholders(p.Status.Count, binds, p.Status)).Append(')');
        if (p.ProductCategory is { Count: > 0 })
            sb.Append(" AND product_category IN (").Append(Placeholders(p.ProductCategory.Count, binds, p.ProductCategory)).Append(')');
        if (p.State is { Count: > 0 })
            sb.Append(" AND state IN (").Append(Placeholders(p.State.Count, binds, p.State)).Append(')');

        if (!string.IsNullOrWhiteSpace(p.Q) && p.Q.Trim().Length >= 2)
        {
            var q = "%" + p.Q.Trim() + "%";
            sb.Append(@" AND (
                service_number LIKE ? OR
                product_name LIKE ? OR
                city LIKE ? OR
                state LIKE ? OR
                address LIKE ? OR
                assignee LIKE ? OR
                notes LIKE ?)");
            for (int i = 0; i < 7; i++) binds.Add(q);
        }

        return (sb.ToString(), binds.ToArray());
    }

    private static string Placeholders(int n, List<object> binds, IEnumerable<string> values)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var v in values)
        {
            if (i++ > 0) sb.Append(", ");
            sb.Append('?');
            binds.Add(v);
        }
        return sb.ToString();
    }

    private static string BuildOrderBy(string? sort, string? dir)
    {
        var col = sort switch
        {
            "createdAt"     => "created_at",
            "updatedAt"     => "updated_at",
            "status"        => "status",
            "serviceNumber" => "service_number",
            "productName"   => "product_name",
            _               => "created_at",
        };
        var d = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        // Tiebreak on id keeps results stable across pages.
        return $"{col} {d}, id {d}";
    }

    // ---- Row shaping ---------------------------------------------------

    private static SnapshotRow ToSnapshotRow(InventoryRow r) => new(
        Id: r.Id,
        ServiceNumber: r.ServiceNumber,
        ProductCategory: r.ProductCategory,
        ProductName: r.ProductName,
        Status: r.Status,
        City: r.City,
        State: r.State,
        Address: r.Address,
        Assignee: r.Assignee,
        Notes: r.Notes,
        CreatedAt: r.CreatedAt,
        UpdatedAt: r.UpdatedAt,
        RowVersion: r.RowVersion,
        DeletedAt: null);

    private async Task UpsertBatchAsync(IReadOnlyList<SnapshotRow> rows, CancellationToken ct)
    {
        if (_dbName is null || rows.Count == 0) return;
        // Reshape to snake_case + string timestamps so the JS bulk-upsert
        // path can bind them directly to the prepared statement.
        var payload = new object[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            payload[i] = new
            {
                id = r.Id,
                service_number = r.ServiceNumber,
                product_category = r.ProductCategory,
                product_name = r.ProductName,
                status = r.Status,
                city = r.City,
                state = r.State,
                address = r.Address,
                assignee = r.Assignee,
                notes = r.Notes,
                created_at = r.CreatedAt.ToString("O"),
                updated_at = r.UpdatedAt.ToString("O"),
                row_version = r.RowVersion,
                deleted_at = r.DeletedAt?.ToString("O"),
            };
        }
        var json = JsonSerializer.Serialize(payload);
        await _js.InvokeAsync<int>("bruinDb.bulkUpsert", ct, _dbName, json);
    }

    // ---- JS helpers ----------------------------------------------------

    private async Task<long> ScalarLongAsync(string sql, CancellationToken ct = default)
        => await ScalarLongAsync(sql, Array.Empty<object>(), ct);

    private async Task<long> ScalarLongAsync(string sql, object[] binds, CancellationToken ct = default)
    {
        var val = await _js.InvokeAsync<JsonElement>("bruinDb.scalar", ct, _dbName, sql, binds);
        return val.ValueKind switch
        {
            JsonValueKind.Number => val.GetInt64(),
            JsonValueKind.String => long.TryParse(val.GetString(), out var n) ? n : 0,
            _ => 0,
        };
    }

    private async Task<string?> ScalarStringAsync(string sql, CancellationToken ct = default)
    {
        var val = await _js.InvokeAsync<JsonElement>("bruinDb.scalar", ct, _dbName, sql, Array.Empty<object>());
        return val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private async Task<IReadOnlyList<InventoryRow>> QueryRowsAsync(
        string sql, object[] binds, CancellationToken ct)
    {
        var json = await _js.InvokeAsync<string>("bruinDb.query", ct, _dbName, sql, binds);
        using var doc = JsonDocument.Parse(json);
        var list = new List<InventoryRow>(doc.RootElement.GetArrayLength());
        foreach (var el in doc.RootElement.EnumerateArray())
            list.Add(RowFromJson(el));
        return list;
    }

    private static InventoryRow RowFromJson(JsonElement el) => new(
        Id:              el.GetProperty("id").GetString() ?? "",
        ServiceNumber:   el.GetProperty("service_number").GetString() ?? "",
        ProductCategory: el.GetProperty("product_category").GetString() ?? "",
        ProductName:     el.GetProperty("product_name").GetString() ?? "",
        Status:          el.GetProperty("status").GetString() ?? "",
        City:            NullableString(el, "city"),
        State:           NullableString(el, "state"),
        Address:         NullableString(el, "address"),
        Assignee:        NullableString(el, "assignee"),
        Notes:           NullableString(el, "notes"),
        CreatedAt:       ParseTs(el, "created_at"),
        UpdatedAt:       ParseTs(el, "updated_at"),
        RowVersion:      el.GetProperty("row_version").GetInt32());

    private static string? NullableString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset ParseTs(JsonElement el, string name)
    {
        // SQLite stores our timestamps as ISO-8601 text (we insert
        // DateTimeOffset.ToString("O")). Parse back into a proper offset.
        var s = el.GetProperty(name).GetString();
        return DateTimeOffset.TryParse(s, out var d) ? d : default;
    }

    private static (DateTimeOffset?, string?) ParseWatermark(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (null, null);
        var bar = raw.IndexOf('|');
        if (bar <= 0) return (null, null);
        var tsPart = raw[..bar];
        var idPart = raw[(bar + 1)..];
        var ok = DateTimeOffset.TryParse(tsPart, out var ts);
        return (ok ? ts : null, string.IsNullOrEmpty(idPart) ? null : idPart);
    }

    // ---- Local schema ---------------------------------------------------

    // Local schema mirrors the server table minus the write-side triggers.
    // Indexes cover the sort keys the grid uses plus the delete filter.
    // Text collations stay default (BINARY) — the API side uses `simple`
    // config for tsvector, which is case-insensitive via the `simple`
    // dictionary; here we replicate that with LOWER() in the LIKE clauses
    // if needed later. For now case-sensitive is fine — LocalReplica.tests
    // will flag it if the parity gap matters.
    private const string EnsureSchemaSql = @"
        CREATE TABLE IF NOT EXISTS inventory (
            id               TEXT PRIMARY KEY,
            service_number   TEXT NOT NULL,
            product_category TEXT NOT NULL,
            product_name     TEXT NOT NULL,
            status           TEXT NOT NULL,
            city             TEXT,
            state            TEXT,
            address          TEXT,
            assignee         TEXT,
            notes            TEXT,
            created_at       TEXT NOT NULL,
            updated_at       TEXT NOT NULL,
            row_version      INTEGER NOT NULL,
            deleted_at       TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_inv_created ON inventory (created_at DESC, id DESC) WHERE deleted_at IS NULL;
        CREATE INDEX IF NOT EXISTS ix_inv_updated ON inventory (updated_at DESC, id DESC) WHERE deleted_at IS NULL;
        CREATE INDEX IF NOT EXISTS ix_inv_status  ON inventory (status, created_at DESC) WHERE deleted_at IS NULL;
        CREATE INDEX IF NOT EXISTS ix_inv_service ON inventory (service_number) WHERE deleted_at IS NULL;
    ";
}

public sealed record HydrationProgress(
    int Batches,
    long RowsThisRun,
    long RowsInMirror,
    bool HasMore,
    long LastBatchMs);
