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

    // Open DB descriptor + last-known watermark for delta pulls. Rebuilt
    // per tenant.
    private Guid? _clientId;
    private string? _dbName;
    private DateTimeOffset? _watermark;
    private string? _watermarkId;
    private long _rowCount;
    private DateTimeOffset? _lastSyncAt;

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
        // fresh tenant lands here with rowCount=0 and null watermark.
        _rowCount = await ScalarLongAsync("SELECT COUNT(*) FROM inventory", ct);
        var wm = await ScalarStringAsync(
            "SELECT COALESCE(MAX(updated_at), '') || '|' || COALESCE((" +
            "  SELECT id FROM inventory WHERE updated_at = (SELECT MAX(updated_at) FROM inventory) " +
            "  ORDER BY id DESC LIMIT 1), '') " +
            "FROM inventory", ct);
        (_watermark, _watermarkId) = ParseWatermark(wm);
        Changed?.Invoke();
    }

    public async Task CloseAsync()
    {
        // We don't close the underlying handle — SAH-pool keeps it open
        // for reuse across component re-renders. Clearing local state is
        // enough. `wipe` is the explicit "delete the file" path (below).
        _clientId = null;
        _dbName = null;
        _watermark = null;
        _watermarkId = null;
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

    // Pulls the snapshot feed to catch up to the server. Yields progress
    // after each successful batch. Safe to call repeatedly; a no-op call
    // is fast because the watermark short-circuits the server's WHERE.
    public async Task HydrateAsync(
        IProgress<HydrationProgress>? progress,
        CancellationToken ct = default)
    {
        if (_dbName is null) throw new InvalidOperationException("Call OpenAsync first.");

        int batches = 0;
        long total = 0;
        while (!ct.IsCancellationRequested)
        {
            SnapshotResponse batch;
            try
            {
                batch = await _api.SnapshotAsync(_watermark, _watermarkId, limit: 5000, ct);
            }
            catch (ApiException ex)
            {
                _errors.Report(ex, "snapshot");
                throw;
            }

            if (batch.Rows.Count > 0)
            {
                await UpsertBatchAsync(batch.Rows, ct);
                _watermark = batch.NextSince ?? _watermark;
                _watermarkId = batch.NextSinceId ?? _watermarkId;
                total += batch.Rows.Count;
                batches++;
            }
            _lastSyncAt = DateTimeOffset.UtcNow;
            _rowCount = await ScalarLongAsync(
                "SELECT COUNT(*) FROM inventory WHERE deleted_at IS NULL", ct);
            progress?.Report(new HydrationProgress(
                Batches: batches,
                RowsThisRun: total,
                RowsInMirror: _rowCount,
                HasMore: batch.HasMore,
                LastBatchMs: batch.TookMs));
            Changed?.Invoke();

            if (!batch.HasMore) break;
        }
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
