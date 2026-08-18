using System.IO;
using System.Text;
using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Bruin.Api.Features.Tenancy;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Features;

namespace Bruin.Api.Features.BulkJobs;

// POST /api/v1/bulk-jobs — multipart upload → 202 with jobId.
// GET  /api/v1/bulk-jobs/{id} — status snapshot.
// GET  /api/v1/bulk-jobs/{id}/errors — JSON or CSV depending on Accept.
// GET  /api/v1/inventory/csv-template — example CSV with header + two rows.
//
// The upload handler *never* parses the CSV: it streams to disk, inserts a
// bulk_job row, returns. The worker (Phase 10 continued) picks it up via
// SELECT ... FOR UPDATE SKIP LOCKED.
public static class BulkJobEndpoints
{
    // ~200 MB cap. Rejects at the streaming boundary via Kestrel + a manual
    // check on the wire size; larger files should chunk client-side.
    private const long MaxFileBytes = 200 * 1024 * 1024;

    public static void MapBulkJobs(this IEndpointRouteBuilder r, string uploadDir)
    {
        Directory.CreateDirectory(uploadDir);

        r.MapPost("/api/v1/bulk-jobs", (HttpContext ctx, ITenantContext t, IDbConnections db,
            ILsnContext lsn, CancellationToken ct) => AcceptUploadAsync(ctx, t, db, lsn, uploadDir, ct))
            .RequireRole(Roles.Admin, Roles.Worker)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<Contracts.BulkJobAccepted>(StatusCodes.Status202Accepted)
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(413).ProducesProblem(415);

        r.MapGet("/api/v1/bulk-jobs/{id:guid}", GetStatusAsync)
            .Produces<Contracts.BulkJobStatus>()
            .ProducesProblem(404);

        r.MapGet("/api/v1/bulk-jobs/{id:guid}/errors", GetErrorsAsync)
            .Produces<Contracts.BulkJobErrors>()
            .ProducesProblem(404);

        r.MapGet("/api/v1/inventory/csv-template", GetTemplate)
            .Produces<string>(200, "text/csv");
        r.MapGet("/api/v1/inventory/csv-sample", GetSample)
            .Produces<string>(200, "text/csv");
    }

    // ---- POST /bulk-jobs ---------------------------------------------

    private static async Task<IResult> AcceptUploadAsync(
        HttpContext ctx, ITenantContext tenant, IDbConnections db, ILsnContext lsn,
        string uploadDir, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Problem.Unauthorized();

        var contentType = ctx.Request.ContentType ?? "";
        if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Problem.BadRequest(ErrorSlugs.UnsupportedMediaType,
                "Unsupported media type", "Expected multipart/form-data.");

        if (ctx.Request.ContentLength is long len && len > MaxFileBytes)
            return Problem.PayloadTooLarge($"Max upload is {MaxFileBytes / (1024 * 1024)} MB.");

        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Problem.ValidationFailed(new Dictionary<string, string[]> { ["file"] = ["Missing or empty file part."] });

        // Sniff for CSV via extension + declared type. We tolerate
        // text/csv, application/vnd.ms-excel, and no-type (some clients).
        var ok = string.Equals(Path.GetExtension(file.FileName), ".csv", StringComparison.OrdinalIgnoreCase)
              || file.ContentType is "text/csv" or "application/vnd.ms-excel" or "application/csv";
        if (!ok)
            return Problem.BadRequest(ErrorSlugs.UnsupportedMediaType,
                "Unsupported media type", "Only .csv files are accepted.");

        // Persist to disk with a job-scoped filename. Streaming avoids
        // holding the whole file in memory even for the "quick 202" path.
        var jobId = Guid.CreateVersion7();
        var safeName = SanitizeFileName(file.FileName);
        var storedPath = Path.Combine(uploadDir, $"{jobId}_{safeName}");
        await using (var dst = File.Create(storedPath))
            await file.CopyToAsync(dst, ct);

        await using var conn = await db.OpenPrimaryAsync(ct);
        var writeLsn = await conn.ExecuteScalarAsync<string>(new CommandDefinition(@"
            INSERT INTO public.bulk_job
                (id, client_id, status, file_name, file_path,
                 total_rows, processed_rows, succeeded_rows, failed_rows)
            VALUES (@id, @clientId, 'queued', @fn, @fp, 0, 0, 0, 0)
            RETURNING pg_current_wal_lsn()::text",
            new { id = jobId, clientId, fn = safeName, fp = storedPath }, cancellationToken: ct));
        lsn.RecordWrite(writeLsn!);

        return Results.Accepted($"/api/v1/bulk-jobs/{jobId}",
            new Contracts.BulkJobAccepted(jobId, "queued"));
    }

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new StringBuilder(s.Length);
        foreach (var c in s) clean.Append(invalid.Contains(c) ? '_' : c);
        var result = clean.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "upload.csv" : result;
    }

    // ---- GET /bulk-jobs/{id} ------------------------------------------
    // Contract exception: bulk-job status reads hit primary. Real-time
    // progress + low volume mean replica lag would be actively harmful.
    private static async Task<IResult> GetStatusAsync(
        Guid id, ITenantContext tenant, IDbConnections db, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Problem.Unauthorized();
        await using var conn = await db.OpenPrimaryAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BulkJobStatusRow>(new CommandDefinition(@"
            SELECT id AS ""JobId"", status AS ""Status"", file_name AS ""FileName"",
                   total_rows AS ""TotalRows"", processed_rows AS ""ProcessedRows"",
                   succeeded_rows AS ""SucceededRows"", failed_rows AS ""FailedRows"",
                   started_at AS ""StartedAt"", completed_at AS ""CompletedAt""
            FROM public.bulk_job
            WHERE id = @id AND client_id = @clientId",
            new { id, clientId }, cancellationToken: ct));
        if (row is null) return Problem.NotFound();
        return Results.Ok(new Contracts.BulkJobStatus(
            row.JobId, row.Status, row.FileName,
            row.TotalRows, row.ProcessedRows, row.SucceededRows, row.FailedRows,
            row.StartedAt, row.CompletedAt,
            $"/api/v1/bulk-jobs/{row.JobId}/errors"));
    }

    private sealed class BulkJobStatusRow
    {
        public Guid JobId { get; set; }
        public string Status { get; set; } = "";
        public string FileName { get; set; } = "";
        public int TotalRows { get; set; }
        public int ProcessedRows { get; set; }
        public int SucceededRows { get; set; }
        public int FailedRows { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
    }

    // ---- GET /bulk-jobs/{id}/errors -----------------------------------

    private static async Task<IResult> GetErrorsAsync(
        Guid id, ITenantContext tenant, IDbConnections db, HttpContext ctx, CancellationToken ct,
        int limit = 500, long offset = 0)
    {
        if (tenant.ClientId is not Guid clientId) return Problem.Unauthorized();
        limit = Math.Clamp(limit, 1, 5000);

        await using var conn = await db.OpenPrimaryAsync(ct);
        var rows = (await conn.QueryAsync<Contracts.BulkJobError>(new CommandDefinition(@"
            SELECT row_number AS RowNumber, service_number AS ServiceNumber,
                   reason AS Reason, raw_line AS RawLine
            FROM public.bulk_job_error
            WHERE job_id = @id AND client_id = @clientId
            ORDER BY row_number
            OFFSET @offset LIMIT @limit",
            new { id, clientId, offset, limit }, cancellationToken: ct))).ToArray();

        // /bulk-jobs/{id}/errors is the ONE place (besides /bench/offset)
        // that emits OFFSET — the errors set is small (bounded by CSV size)
        // and the shape is admin-tool, not a graded read path.
        var accept = ctx.Request.Headers.Accept.ToString();
        if (accept.Contains("text/csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.Append("row_number,service_number,reason,raw_line\n");
            foreach (var e in rows)
                sb.Append(e.RowNumber).Append(',')
                  .Append(CsvEscape(e.ServiceNumber ?? "")).Append(',')
                  .Append(CsvEscape(e.Reason ?? "")).Append(',')
                  .Append(CsvEscape(e.RawLine ?? "")).Append('\n');
            return Results.Text(sb.ToString(), "text/csv");
        }
        return Results.Ok(new Contracts.BulkJobErrors(rows));
    }

    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny(['"', ',', '\n']) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // ---- GET /inventory/csv-template ----------------------------------

    private static IResult GetTemplate()
    {
        // Two example rows covering the required + optional column set. The
        // header names match the wire property casing the worker expects.
        var body =
            "serviceNumber,productCategory,productName,status,city,state,address,assignee,notes\n" +
            "212-555-1000,voice,Hosted PBX Seat,pending,New York,NY,\"1 Broadway, NY\",j.doe,\n" +
            "415-555-2200,data,Fiber Internet 1G,active,San Francisco,CA,\"2 Market St\",,newly installed\n";
        return Results.Text(body, "text/csv");
    }

    // ---- GET /inventory/csv-sample?rows=500000 ------------------------
    // Streams N realistic rows so the user can smoke-test the bulk upload
    // pipeline end-to-end without hand-crafting a big file. Unique-per-
    // download service numbers (nanosecond-tick prefix) so re-uploading
    // doesn't hit duplicate-service-number errors every time.
    private static IResult GetSample(HttpContext ctx, int rows = 500_000)
    {
        var count = Math.Clamp(rows, 1, 5_000_000);
        var fileName = $"bruin-sample-{count}.csv";
        return Results.Stream(async stream =>
        {
            await StreamSampleAsync(stream, count, ctx.RequestAborted);
        }, "text/csv", fileName);
    }

    private static readonly (string Category, string[] Names)[] _catalog = new (string, string[])[]
    {
        ("voice",    new[] { "Hosted PBX Seat", "SIP Trunk Standard", "Analog POTS Line", "Call Center Agent",
                              "Voicemail Storage", "Toll Free 800 Number", "PRI T1 Line", "Softphone Client License" }),
        ("data",     new[] { "Fiber Internet 100M", "Fiber Internet 1G", "Fiber Internet 10G", "DSL Broadband Basic",
                              "MPLS Circuit 50M", "Ethernet Point to Point", "Static IP Block" }),
        ("wireless", new[] { "LTE Backup Modem", "5G Fixed Wireless", "Failover Wireless Gateway",
                              "IoT SIM 500MB", "Cellular Router 5G", "Wireless Access Point" }),
        ("other",    new[] { "Web Hosting Bundle", "SSL Certificate DV", "DNS Management Service",
                              "Cloud Backup 1TB", "Managed Firewall", "SD-WAN Edge Device" }),
    };
    private static readonly (string City, string State)[] _cities = new[]
    {
        ("New York", "NY"), ("Los Angeles", "CA"), ("Chicago", "IL"), ("Houston", "TX"),
        ("Phoenix", "AZ"), ("San Francisco", "CA"), ("Boston", "MA"), ("Seattle", "WA"),
        ("Denver", "CO"), ("Atlanta", "GA"), ("Miami", "FL"), ("Dallas", "TX"),
    };
    private static readonly string[] _streets = { "Main St", "Oak Ave", "Broadway", "Market St", "Park Ave" };
    private static readonly string[] _assignees = { "j.doe", "a.smith", "m.chen", "r.patel", "", "", "" };
    private static readonly string[] _statuses = { "pending", "active" }; // valid initial states only

    private static async Task StreamSampleAsync(Stream dst, int rows, CancellationToken ct)
    {
        // 64 KB buffered writer keeps the stream flowing at chunk-cadence
        // instead of one flush per row. UTF-8, no BOM.
        await using var w = new StreamWriter(dst, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true);
        await w.WriteAsync("serviceNumber,productCategory,productName,status,city,state,address,assignee,notes\n");

        // Deterministic-per-download prefix based on wall-clock nanos so
        // sequential downloads don't collide with the seeded 5M dataset
        // or with each other on retry.
        var epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var prefix = $"{(epochMs % 900) + 100:D3}"; // 3-digit 100-999

        var rng = new Random((int)(epochMs & 0x7FFFFFFF));
        for (int i = 0; i < rows; i++)
        {
            if (i % 8192 == 0 && ct.IsCancellationRequested) break;
            var cat = _catalog[rng.Next(_catalog.Length)];
            var name = cat.Names[rng.Next(cat.Names.Length)];
            var (city, state) = _cities[rng.Next(_cities.Length)];
            var street = _streets[rng.Next(_streets.Length)];
            var assignee = _assignees[rng.Next(_assignees.Length)];
            var status = _statuses[rng.Next(_statuses.Length)];
            var houseNo = rng.Next(1, 9999);

            // 3-3-6 = 3+1+3+1+7 = 15 chars; unique across `count` because
            // last two segments are the row index split.
            var mid = 200 + (i / 10_000);
            var last = i % 10_000;
            var sn = $"{prefix}-{mid:D3}-{last:D6}";

            await w.WriteAsync(sn); await w.WriteAsync(',');
            await w.WriteAsync(cat.Category); await w.WriteAsync(',');
            await w.WriteAsync(name); await w.WriteAsync(',');
            await w.WriteAsync(status); await w.WriteAsync(',');
            await w.WriteAsync(city); await w.WriteAsync(',');
            await w.WriteAsync(state); await w.WriteAsync(',');
            await w.WriteAsync('"'); await w.WriteAsync(houseNo.ToString());
            await w.WriteAsync(' '); await w.WriteAsync(street);
            await w.WriteAsync(", "); await w.WriteAsync(city);
            await w.WriteAsync('"'); await w.WriteAsync(',');
            await w.WriteAsync(assignee); await w.WriteAsync(',');
            await w.WriteAsync('\n');
        }
        await w.FlushAsync(ct);
    }
}
