using System.Globalization;
using System.Text;
using Bruin.Api.Domain;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Bruin.Api.Features.BulkJobs;

// BackgroundService that drains bulk-job rows. Runs in the --worker image;
// multiple workers can run in parallel because job claim uses
// SELECT ... FOR UPDATE SKIP LOCKED — no coordinator, no leader election.
//
// Per-chunk pipeline:
//   1. Parse + validate 5,000 rows locally, collecting per-row errors.
//   2. Binary COPY the good rows into a session-scoped TEMP staging table.
//   3. INSERT ... SELECT ... FROM staging ON CONFLICT (client_id, service_number)
//      DO NOTHING RETURNING id — the row count tells us how many stuck.
//      Rows that didn't stick were either in-file dups or pre-existing.
//   4. In the SAME transaction, bump processed_rows / succeeded_rows /
//      failed_rows. Crash-safe: a kill between chunks re-runs the chunk
//      but ON CONFLICT DO NOTHING keeps it idempotent, and the checkpoint
//      is the truth. See Phase 10 gate ("resume identical count").
public sealed class BulkJobRunner : BackgroundService
{
    private readonly string _primaryConn;
    private readonly ILogger<BulkJobRunner> _log;
    private const int ChunkSize = 5_000;
    private const int PollDelayMs = 500;

    public BulkJobRunner(string primaryConn, ILogger<BulkJobRunner> log)
    {
        _primaryConn = primaryConn;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("BulkJobRunner started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var job = await ClaimNextJobAsync(ct);
                if (job is null)
                {
                    await Task.Delay(PollDelayMs, ct);
                    continue;
                }
                _log.LogInformation("processing bulk job {JobId} file={FileName}", job.Id, job.FileName);
                await ProcessJobAsync(job, ct);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "worker loop error");
                try { await Task.Delay(2_000, ct); } catch { }
            }
        }
    }

    // ---- Claim: single UPDATE that both selects and marks processing ---
    // We use a CTE with SKIP LOCKED so N workers pick distinct jobs.
    private async Task<Job?> ClaimNextJobAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_primaryConn);
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Job>(new CommandDefinition(@"
            WITH picked AS (
                SELECT id
                FROM public.bulk_job
                WHERE status IN ('queued','processing')
                ORDER BY created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE public.bulk_job b
            SET status = 'processing',
                started_at = COALESCE(started_at, now())
            FROM picked
            WHERE b.id = picked.id
            RETURNING b.id AS Id, b.client_id AS ClientId, b.file_path AS FilePath,
                      b.file_name AS FileName, b.processed_rows AS ProcessedRows",
            cancellationToken: ct));
    }

    private sealed class Job
    {
        public Guid Id { get; set; }
        public Guid ClientId { get; set; }
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public int ProcessedRows { get; set; }
    }

    // ---- Process the file in 5k chunks, resuming from checkpoint -------
    private async Task ProcessJobAsync(Job job, CancellationToken ct)
    {
        if (!File.Exists(job.FilePath))
        {
            await MarkFailedAsync(job.Id, "file missing on disk", ct);
            return;
        }

        // Read the header first so we know the column order.
        using var reader = new StreamReader(job.FilePath, Encoding.UTF8);
        var header = await reader.ReadLineAsync(ct);
        if (header is null)
        {
            await MarkFailedAsync(job.Id, "empty file", ct);
            return;
        }
        var columns = ParseCsvRow(header);
        var idx = ColumnIndex(columns);
        if (idx.ServiceNumber < 0 || idx.ProductCategory < 0 || idx.ProductName < 0 || idx.Status < 0)
        {
            await MarkFailedAsync(job.Id, "csv header must include serviceNumber, productCategory, productName, status", ct);
            return;
        }

        // Skip already-processed rows for resume.
        int skipped = 0;
        while (skipped < job.ProcessedRows)
        {
            if (await reader.ReadLineAsync(ct) is null) break;
            skipped++;
        }

        // Read remainder in 5k chunks.
        int rowNumber = 1 + skipped; // 1-based inclusive of header
        var chunkBuffer = new List<(int RowNo, string RawLine, ValidatedRow? Row, string? Reason)>(ChunkSize);
        var chunkOnlyErrors = new List<(int RowNo, string RawLine, string? Sn, string Reason)>();
        int chunkSucceeded = 0, chunkFailed = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parsed = TryValidateRow(line, idx, job.ClientId, out var reason);
            if (parsed is null) chunkOnlyErrors.Add((rowNumber, line, ExtractSn(line, idx), reason ?? "invalid row"));
            chunkBuffer.Add((rowNumber, line, parsed, reason));

            if (chunkBuffer.Count >= ChunkSize)
            {
                var (ok, failedRow) = await FlushChunkAsync(job, chunkBuffer, chunkOnlyErrors, ct);
                chunkSucceeded += ok;
                chunkFailed += failedRow;
                chunkBuffer.Clear();
                chunkOnlyErrors.Clear();
            }
        }

        if (chunkBuffer.Count > 0)
        {
            var (ok, failedRow) = await FlushChunkAsync(job, chunkBuffer, chunkOnlyErrors, ct);
            chunkSucceeded += ok;
            chunkFailed += failedRow;
        }

        await MarkTerminalAsync(job.Id, ct);
        _log.LogInformation("bulk job {JobId} complete: succeeded={Ok} failed={Fail}",
            job.Id, chunkSucceeded, chunkFailed);
    }

    // ---- Chunk flush: staging COPY + upsert + checkpoint in ONE txn ----
    private async Task<(int succeeded, int failed)> FlushChunkAsync(
        Job job,
        IReadOnlyList<(int RowNo, string RawLine, ValidatedRow? Row, string? Reason)> chunk,
        IReadOnlyList<(int RowNo, string RawLine, string? Sn, string Reason)> parseErrors,
        CancellationToken ct)
    {
        var valid = chunk.Where(c => c.Row is not null).Select(c => c.Row!).ToArray();

        await using var conn = new NpgsqlConnection(_primaryConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Staging table lives for the txn — COPY into it, then INSERT SELECT
        // with ON CONFLICT DO NOTHING. Rows that fail the ON CONFLICT are
        // "pre-existing or in-file duplicates" — we count them as errors.
        await Exec(conn, tx, @"CREATE TEMP TABLE stg_inv (
            id uuid, client_id uuid, service_number varchar(64),
            product_category varchar(16), product_name varchar(200),
            status varchar(16), city varchar(120), state varchar(2),
            address varchar(300), assignee varchar(120), notes text
        ) ON COMMIT DROP");

        if (valid.Length > 0)
        {
            using var w = await conn.BeginBinaryImportAsync(
                "COPY stg_inv (id, client_id, service_number, product_category, product_name, status, city, state, address, assignee, notes) FROM STDIN (FORMAT BINARY)",
                ct);
            foreach (var r in valid)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(r.Id, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(job.ClientId, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(r.ServiceNumber, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(r.ProductCategory, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(r.ProductName, NpgsqlDbType.Varchar, ct);
                await w.WriteAsync(r.Status, NpgsqlDbType.Varchar, ct);
                if (r.City is null) await w.WriteNullAsync(ct); else await w.WriteAsync(r.City, NpgsqlDbType.Varchar, ct);
                if (r.State is null) await w.WriteNullAsync(ct); else await w.WriteAsync(r.State, NpgsqlDbType.Varchar, ct);
                if (r.Address is null) await w.WriteNullAsync(ct); else await w.WriteAsync(r.Address, NpgsqlDbType.Varchar, ct);
                if (r.Assignee is null) await w.WriteNullAsync(ct); else await w.WriteAsync(r.Assignee, NpgsqlDbType.Varchar, ct);
                if (r.Notes is null) await w.WriteNullAsync(ct); else await w.WriteAsync(r.Notes, NpgsqlDbType.Text, ct);
            }
            await w.CompleteAsync(ct);
        }

        // Deduplicate WITHIN the staging batch by keeping the FIRST occurrence
        // per (client_id, service_number). Everything else falls through
        // to the errors list below.
        // ON CONFLICT DO NOTHING silently drops rows that duplicate an
        // already-existing row on (client_id, service_number).
        var inserted = valid.Length == 0 ? 0 : await conn.ExecuteScalarAsync<int>(new CommandDefinition(@"
            WITH deduped AS (
                SELECT DISTINCT ON (client_id, service_number) *
                FROM stg_inv
                ORDER BY client_id, service_number, id
            ),
            ins AS (
                INSERT INTO public.inventory
                    (id, client_id, service_number, product_category, product_name, status,
                     city, state, address, assignee, notes)
                SELECT id, client_id, service_number, product_category, product_name, status,
                       city, state, address, assignee, notes
                FROM deduped
                ON CONFLICT (client_id, service_number)
                    WHERE deleted_at IS NULL
                    DO NOTHING
                RETURNING id
            )
            SELECT count(*)::int FROM ins", transaction: tx, cancellationToken: ct));

        // Winner-ids: staging rows whose exact id landed in inventory. Every
        // OTHER valid row (same client_id + service_number as a winner) is a
        // loser and gets a per-row duplicate error. Rows that duplicate an
        // already-existing DB row have no winner in stg — inventory.id for
        // that SN is neither in stg_inv nor equal to any staging row.
        var winnerIds = valid.Length == 0
            ? new HashSet<Guid>()
            : new HashSet<Guid>(await conn.QueryAsync<Guid>(new CommandDefinition(@"
                SELECT s.id
                FROM stg_inv s
                JOIN public.inventory i
                  ON i.client_id = s.client_id
                 AND i.service_number = s.service_number
                 AND i.id = s.id",
                transaction: tx, cancellationToken: ct)));

        // Persist per-row errors — parse errors + duplicates. A row is a
        // duplicate iff it was validated (has .Row) AND its staging id is
        // not in the winner set.
        var errorRows = new List<(int RowNo, string? Sn, string Reason, string RawLine)>();
        foreach (var e in parseErrors) errorRows.Add((e.RowNo, e.Sn, e.Reason, e.RawLine));
        foreach (var c in chunk.Where(c => c.Row is not null && !winnerIds.Contains(c.Row!.Id)))
            errorRows.Add((c.RowNo, c.Row!.ServiceNumber, "duplicate service_number", c.RawLine));

        if (errorRows.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(@"
                INSERT INTO public.bulk_job_error
                    (job_id, client_id, row_number, service_number, reason, raw_line)
                VALUES (@jobId, @clientId, @row, @sn, @reason, @raw)",
                errorRows.Select(e => new
                {
                    jobId = job.Id, clientId = job.ClientId,
                    row = e.RowNo, sn = e.Sn, reason = e.Reason, raw = e.RawLine,
                }), transaction: tx, cancellationToken: ct));
        }

        // Checkpoint in the same txn. Resume-safe: if the txn commits, the
        // checkpoint reflects the actual work; if the worker dies before
        // commit, the txn rolls back and the whole chunk is re-run from
        // the last committed processed_rows.
        var processedIncr = chunk.Count;
        var failed = chunk.Count - inserted;
        await conn.ExecuteAsync(new CommandDefinition(@"
            UPDATE public.bulk_job
            SET processed_rows = processed_rows + @proc,
                succeeded_rows = succeeded_rows + @ok,
                failed_rows    = failed_rows + @fail,
                total_rows     = GREATEST(total_rows, processed_rows + @proc)
            WHERE id = @id",
            new { id = job.Id, proc = processedIncr, ok = inserted, fail = failed },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return (inserted, failed);
    }

    private async Task MarkTerminalAsync(Guid jobId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_primaryConn);
        await conn.OpenAsync(ct);
        await Exec(conn, null, @"
            UPDATE public.bulk_job
            SET status = CASE
                    WHEN failed_rows > 0 THEN 'completedWithErrors'
                    ELSE 'completed'
                END,
                completed_at = now()
            WHERE id = '" + jobId + "'::uuid");

        // Refresh planner statistics so the grid's per-tenant "Table total"
        // reflects the new row count. Plain INSERT doesn't move pg_statistic
        // — autovacuum ANALYZE only trips at ~10 % churn, which for a 5 M-row
        // table means ~500 k inserts. A bulk job of any size might land under
        // that threshold, so nudge the planner + the TenantRowEstimator
        // (which reads EXPLAIN Plan Rows off pg_statistic) here.
        // Cost is a stats sample — bounded, ~seconds even on the 5 M table.
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "ANALYZE public.inventory", cancellationToken: ct));
        }
        catch (Exception ex)
        {
            // ANALYZE is best-effort — a failure only means the count stays
            // stale until autovacuum catches up. Don't fail the job for it.
            _log.LogWarning(ex, "post-job ANALYZE failed for {JobId}", jobId);
        }
    }

    private async Task MarkFailedAsync(Guid jobId, string reason, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_primaryConn);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE public.bulk_job SET status = 'failed', completed_at = now() WHERE id = @id",
            new { id = jobId }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO public.bulk_job_error (job_id, client_id, row_number, service_number, reason, raw_line)
            SELECT id, client_id, 0, NULL, @reason, '' FROM public.bulk_job WHERE id = @id",
            new { id = jobId, reason }, cancellationToken: ct));
    }

    // ---- Row parsing + validation ------------------------------------

    private sealed record ValidatedRow(
        Guid Id, string ServiceNumber, string ProductCategory, string ProductName, string Status,
        string? City, string? State, string? Address, string? Assignee, string? Notes);

    private sealed class Idx
    {
        public int ServiceNumber = -1, ProductCategory = -1, ProductName = -1, Status = -1;
        public int City = -1, State = -1, Address = -1, Assignee = -1, Notes = -1;
    }

    private static Idx ColumnIndex(string[] headers)
    {
        var i = new Idx();
        for (int j = 0; j < headers.Length; j++)
        {
            switch (headers[j].Trim())
            {
                case "serviceNumber":   i.ServiceNumber = j; break;
                case "productCategory": i.ProductCategory = j; break;
                case "productName":     i.ProductName = j; break;
                case "status":          i.Status = j; break;
                case "city":            i.City = j; break;
                case "state":           i.State = j; break;
                case "address":         i.Address = j; break;
                case "assignee":        i.Assignee = j; break;
                case "notes":           i.Notes = j; break;
            }
        }
        return i;
    }

    private static string? ExtractSn(string line, Idx idx)
    {
        try { return ParseCsvRow(line)[idx.ServiceNumber]; }
        catch { return null; }
    }

    private static ValidatedRow? TryValidateRow(string line, Idx idx, Guid clientId, out string? reason)
    {
        reason = null;
        string[] cells;
        try { cells = ParseCsvRow(line); }
        catch (Exception ex) { reason = "csv parse: " + ex.Message; return null; }

        string get(int j) => j >= 0 && j < cells.Length ? cells[j].Trim() : "";
        string? getOpt(int j) { var v = get(j); return string.IsNullOrEmpty(v) ? null : v; }

        var sn = get(idx.ServiceNumber);
        var cat = get(idx.ProductCategory);
        var name = get(idx.ProductName);
        var status = get(idx.Status);

        if (string.IsNullOrWhiteSpace(sn)) { reason = "serviceNumber required"; return null; }
        if (!ProductCategories.All.Contains(cat)) { reason = $"invalid productCategory '{cat}'"; return null; }
        if (string.IsNullOrWhiteSpace(name)) { reason = "productName required"; return null; }
        // Contract: creating disconnected is invalid; only pending/active are valid initial states.
        if (status != InventoryStatuses.Pending && status != InventoryStatuses.Active)
        { reason = $"invalid initial status '{status}' (must be pending or active)"; return null; }

        return new ValidatedRow(
            Id: Guid.CreateVersion7(),
            ServiceNumber: sn, ProductCategory: cat, ProductName: name, Status: status,
            City: getOpt(idx.City), State: getOpt(idx.State),
            Address: getOpt(idx.Address), Assignee: getOpt(idx.Assignee),
            Notes: getOpt(idx.Notes));
    }

    private static async Task Exec(NpgsqlConnection c, System.Data.Common.DbTransaction? tx, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx as NpgsqlTransaction;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // Minimal RFC 4180 CSV parser — handles quotes, embedded commas, and
    // doubled quotes. Line-scoped (no cross-line records) which is fine
    // for the fun-times's fixed-column format.
    internal static string[] ParseCsvRow(string line)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cur.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }
}
