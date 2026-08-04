using System.Text;
using System.Text.Json;
using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bruin.Api.Features.BulkJobs;

// GET /api/v1/bulk-jobs/{id}/events — Server-Sent Events driven by polling
// the bulk_job row every 500 ms. Deliberately polling (not an in-process
// event bus) so any API replica can serve any client's stream — the client
// can reconnect to a different pod mid-job without state loss. Contract
// clients that see an errored stream fall back to plain GET polling.
public static class BulkJobEvents
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    // Match the app's default camelCase policy so the SSE payload has the
    // same field shape as `GET /bulk-jobs/{id}` — the UI treats the two
    // interchangeably (polling fallback path).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapBulkJobEvents(this IEndpointRouteBuilder r)
    {
        r.MapGet("/api/v1/bulk-jobs/{id:guid}/events", StreamAsync);
    }

    private static async Task StreamAsync(
        Guid id, HttpContext ctx, ITenantContext tenant, IDbConnections db)
    {
        if (tenant.ClientId is not Guid clientId)
        {
            ctx.Response.StatusCode = 401; return;
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache, no-transform";
        // Some proxies buffer SSE; the Nginx-friendly header disables it.
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var startedAt = DateTime.UtcNow;
        var lastPayload = "";
        var ct = ctx.RequestAborted;

        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow - startedAt < MaxDuration)
            {
                var snap = await ReadJob(db, clientId, id, ct);
                if (snap is null)
                {
                    await WriteEvent(ctx, "error", JsonSerializer.Serialize(new { code = "not-found" }, JsonOpts));
                    return;
                }

                var json = JsonSerializer.Serialize(snap, JsonOpts);
                if (json != lastPayload)
                {
                    await WriteEvent(ctx, "progress", json);
                    lastPayload = json;
                }

                // Terminal state — one final `done` frame with the full
                // status so the client can render the summary without an
                // extra GET.
                if (snap.Status is "completed" or "completedWithErrors" or "failed")
                {
                    await WriteEvent(ctx, "done", json);
                    return;
                }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch (TaskCanceledException) { /* client disconnected */ }
        catch (OperationCanceledException) { /* same */ }
    }

    private static async Task<Contracts.BulkJobStatus?> ReadJob(
        IDbConnections db, Guid clientId, Guid id, CancellationToken ct)
    {
        // Read from primary — bulk-job status is contract exception (see
        // ReadRouter comments). SSE clients can't tolerate replica lag on
        // progress bars.
        await using var conn = await db.OpenPrimaryAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(@"
            SELECT id AS JobId, status AS Status, file_name AS FileName,
                   total_rows AS TotalRows, processed_rows AS ProcessedRows,
                   succeeded_rows AS SucceededRows, failed_rows AS FailedRows,
                   started_at AS StartedAt, completed_at AS CompletedAt
            FROM public.bulk_job
            WHERE id = @id AND client_id = @clientId",
            new { id, clientId }, cancellationToken: ct));
        return row is null ? null : new Contracts.BulkJobStatus(
            row.JobId, row.Status, row.FileName,
            row.TotalRows, row.ProcessedRows, row.SucceededRows, row.FailedRows,
            row.StartedAt, row.CompletedAt, $"/api/v1/bulk-jobs/{row.JobId}/errors");
    }

    private sealed class Row
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

    private static async Task WriteEvent(HttpContext ctx, string ev, string data)
    {
        var buf = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {data}\n\n");
        await ctx.Response.Body.WriteAsync(buf, ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
}
