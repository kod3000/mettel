using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Features.Inventory;
using Bruin.Api.Features.Tenancy;
using Bruin.Api.Middleware;
using Bruin.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// --worker mode runs only the background worker. Same image, different
// entrypoint arg — keeps the compose stack to one dotnet build.
var isWorker = args.Contains("--worker");

var builder = WebApplication.CreateBuilder(args);

var primaryConn = builder.Configuration.GetConnectionString("Primary")
    ?? Environment.GetEnvironmentVariable("BRUIN_DB_PRIMARY")
    ?? "Host=localhost;Port=5432;Database=bruin;Username=bruin;Password=bruin";
var replicaConn = builder.Configuration.GetConnectionString("Replica")
    ?? Environment.GetEnvironmentVariable("BRUIN_DB_REPLICA")
    ?? primaryConn;

if (isWorker)
{
    // BulkJobRunner drains queued bulk jobs from bulk_job via SELECT ...
    // FOR UPDATE SKIP LOCKED — safely horizontally scalable, no coordinator.
    var host = Host.CreateApplicationBuilder(args);
    host.Services.AddSingleton(sp => new Bruin.Api.Features.BulkJobs.BulkJobRunner(
        primaryConn,
        sp.GetRequiredService<ILogger<Bruin.Api.Features.BulkJobs.BulkJobRunner>>()));
    host.Services.AddHostedService(sp => sp.GetRequiredService<Bruin.Api.Features.BulkJobs.BulkJobRunner>());
    await host.Build().RunAsync();
    return;
}

builder.Services.AddSingleton(new DbEndpoints(primaryConn, replicaConn));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Bulk-upload sizing: Kestrel defaults MaxRequestBodySize to ~30 MB and
// FormOptions.MultipartBodyLengthLimit to ~128 MB. Both need to match
// BulkJobEndpoints.MaxFileBytes (200 MB) or a 500 k CSV (~45 MB) 413s
// before ever reaching the endpoint's own size check. Kept as an explicit
// constant so the three limits (nginx, Kestrel, endpoint) stay aligned.
const long MaxUploadBytes = 200L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUploadBytes;
    o.ValueLengthLimit = int.MaxValue;
});

// Writes go through EF Core against the primary. The graded list-read path
// bypasses this and uses Dapper via IReadRouter → IDbConnections.
builder.Services.AddDbContext<BruinDbContext>(o => o.UseNpgsql(primaryConn,
    npg => npg.MigrationsHistoryTable("__ef_migrations_history")));

// Dual-pool primitives.
builder.Services.AddSingleton<IDbConnections>(_ => new DbConnections(primaryConn, replicaConn));

// Observability: process-local counters + gauges exposed at /metrics.
builder.Services.AddSingleton<Metrics>();

// Cached replica-lag state — singleton so the 250 ms cache is shared across
// all concurrent requests.
builder.Services.AddSingleton<ReplicaState>();

// Cached per-tenant row-count estimate (30 s TTL). Drops the totalEstimate
// probe off the graded list path for cache hits.
builder.Services.AddSingleton<TenantRowEstimator>();

// Scoped per-request context: tenant + LSN watermark.
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<ILsnContext, LsnContext>();
// Local api_key + client resolver, plus a typed HttpClient that asks
// mt-oidc's /resolve for keys the local table doesn't know. The
// decorator (registered as IApiKeyResolver below) tries local first
// and only falls back to identity on a miss — hot path stays offline.
builder.Services.AddScoped<ApiKeyResolver>();
var identityBaseUrl = builder.Configuration["Identity:BaseUrl"]
    ?? Environment.GetEnvironmentVariable("BRUIN_IDENTITY_URL")
    ?? "https://auth.mettel.exercise.dany.codes";
builder.Services.AddHttpClient<IIdentityResolver, HttpIdentityResolver>(c =>
{
    c.BaseAddress = new Uri(identityBaseUrl.TrimEnd('/') + "/");
    // Auth must never block on a slow identity service — fail fast and
    // return a 401 rather than hanging the whole request.
    c.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<IApiKeyResolver, ApiKeyResolverWithFallback>();

// Scoped read router — depends on the scoped LsnContext to pick primary vs
// replica per request. Handlers depend on IReadRouter, not IDbConnections.
builder.Services.AddScoped<IReadRouter, ReadRouter>();

// Cursor codec singleton — the HMAC key is loaded from config so a rolling
// deploy can rotate. Default is fine for dev.
var cursorKey = builder.Configuration["Cursor:HmacKey"]
    ?? Environment.GetEnvironmentVariable("BRUIN_CURSOR_KEY")
    ?? "dev-cursor-key-change-me";
builder.Services.AddSingleton(new CursorCodec(System.Text.Encoding.UTF8.GetBytes(cursorKey)));

builder.Services.AddScoped<ListHandler>();
builder.Services.AddScoped<WriteHandler>();
builder.Services.AddScoped<SnapshotHandler>();

// OpenAPI 3.1 emission (Phase 7). Served at /openapi/v1.json — consumed by
// `packages/api-types` codegen. Kept dev-only by default; production
// deployments can gate behind an env var.
//
// Three document patches keep the generated spec honest:
//   1. Strip `servers`: it echoes `request.Host`, so fetching the spec via
//      `curl localhost:8081` vs `curl 127.0.0.1:8081` produced two committed
//      files and `make verify` flipped on the diff. Codegen doesn't need URLs.
//   2. Add `errors` to ProblemDetails: RFC 7807 validation responses carry a
//      per-field `errors` map (see Errors/Problem.cs → `Extensions["errors"]`),
//      but the built-in ProblemDetails schema doesn't declare it. Without this
//      the SPA has to hand-widen the type in aliases.ts, and the WASM DTO
//      guesses. Declaring it once here keeps every client honest.
//   3. Add `enum` to `status` / `productCategory` on InventoryRow + CreateRequest:
//      the vocabularies are enforced as CHECK constraints and lived in
//      Domain/Inventory.cs; surfacing them on the wire gives clients
//      compile-time narrowing instead of a bare `string`.
builder.Services.AddOpenApi("v1", opts =>
{
    // Strip `servers` — see rationale in the header comment above.
    opts.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Servers?.Clear();
        return Task.CompletedTask;
    });

    // Per-schema mutations. `AddSchemaTransformer` is the .NET 9 API for
    // reliably modifying individual schemas after generation but before
    // serialization; equivalent post-hoc mutation via AddDocumentTransformer
    // doesn't persist for nested schema properties (source-gen path
    // re-emits from the type descriptor).
    opts.AddSchemaTransformer((schema, ctx, _) =>
    {
        // ProblemDetails.errors — see rationale in the header comment.
        if (ctx.JsonTypeInfo.Type == typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))
        {
            schema.Properties["errors"] = new Microsoft.OpenApi.Models.OpenApiSchema
            {
                Type = "object",
                Description = "Per-field validation messages, keyed by property name. Populated on 400 validation-failed responses; absent otherwise.",
                Nullable = true,
                AdditionalProperties = new Microsoft.OpenApi.Models.OpenApiSchema
                {
                    Type = "array",
                    Items = new Microsoft.OpenApi.Models.OpenApiSchema { Type = "string" },
                },
            };
        }

        // Enum narrowing on InventoryRow + CreateRequest — the vocabularies
        // are enforced as CHECK constraints in Domain/Inventory.cs; surfacing
        // them on the wire gives clients compile-time narrowing.
        var t = ctx.JsonTypeInfo.Type;
        if (t == typeof(Bruin.Api.Contracts.InventoryRow) ||
            t == typeof(Bruin.Api.Features.Inventory.CreateRequest))
        {
            ApplyEnum(schema, "status",          Bruin.Api.Domain.InventoryStatuses.All);
            ApplyEnum(schema, "productCategory", Bruin.Api.Domain.ProductCategories.All);
        }

        return Task.CompletedTask;

        static void ApplyEnum(
            Microsoft.OpenApi.Models.OpenApiSchema schema,
            string propertyName, IReadOnlySet<string> values)
        {
            if (schema.Properties is null) return;
            if (!schema.Properties.TryGetValue(propertyName, out var p)) return;
            p.Enum = values
                .Select(v => (Microsoft.OpenApi.Any.IOpenApiAny)new Microsoft.OpenApi.Any.OpenApiString(v))
                .ToList();
        }
    });
});

var app = builder.Build();

// Apply pending migrations on startup. Migrations are idempotent (IF NOT EXISTS
// on extensions; EF's history table guards the rest), so this is safe on both
// empty and already-populated volumes.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BruinDbContext>();
    await db.Database.MigrateAsync();
}

// Middleware order matters: X-Api-Key first (binds tenant), then LSN
// (populates min-LSN and installs the response OnStarting hook that writes
// X-Write-LSN). Both short-circuit before hitting endpoints on failure.
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<LsnMiddleware>();

app.MapGet("/", () => Results.Ok(new { name = "bruin-api", ok = true }));

// /health/live — process is up. Cheap; no DB touch.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

// /health/ready — primary reachable AND replica reachable-and-not-too-lagged.
// Per contract: readiness must fail when the replica is down so a rolling
// deploy doesn't send traffic to a node that will fall back to primary for
// every read. Lag threshold defaults to 5 s (ReplicaState.LagAcceptable).
app.MapGet("/health/ready", async (DbEndpoints eps, ReplicaState replica, CancellationToken ct) =>
{
    var (primaryOk, primaryErr) = await PingAsync(eps.Primary);
    // GetReplayLsnAsync refreshes _reachable + lag reading.
    _ = await replica.GetReplayLsnAsync(ct);
    var replicaOk = replica.IsReachable;
    var lagOk = replica.IsLagAcceptable();
    var payload = new
    {
        status = primaryOk && replicaOk && lagOk ? "ready" : "not_ready",
        primary = new { ok = primaryOk, error = primaryErr },
        replica = new { ok = replicaOk, lagOk }
    };
    return primaryOk && replicaOk && lagOk
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: 503);
});

// /metrics — Prometheus text format. Two custom series today: fallback
// counter and replica-lag gauge. Kestrel/ASPNETCORE emit their own via
// System.Diagnostics.Metrics if a proper exporter is wired later.
app.MapGet("/metrics", (Metrics m) => Results.Text(m.RenderPrometheus(), "text/plain; version=0.0.4"));

app.MapOpenApi("/openapi/{documentName}.json");

app.MapInventory();
Bruin.Api.Features.Tenancy.MeEndpoint.MapMe(app);
Bruin.Api.Features.Debug.LsnEndpoint.MapDebugLsn(app);
Bruin.Api.Features.SavedViews.SavedViewEndpoints.MapSavedViews(app);

// Bulk-job endpoints. Upload directory is shared with the worker via a
// named volume in prod; in dev it's a local path both containers can see.
var uploadDir = Environment.GetEnvironmentVariable("BRUIN_UPLOAD_DIR") ?? "/uploads";
Bruin.Api.Features.BulkJobs.BulkJobEndpoints.MapBulkJobs(app, uploadDir);
Bruin.Api.Features.BulkJobs.BulkJobEvents.MapBulkJobEvents(app);

// Env-gated OFFSET control endpoint (Phase 4 bench only). Contract-invariant:
// this is the only route in the entire API that touches OFFSET.
var benchMode = string.Equals(Environment.GetEnvironmentVariable("BRUIN_BENCH_MODE"),
    "1", StringComparison.Ordinal);
app.MapBenchOffset(benchMode);

app.Run();

static async Task<(bool ok, string? error)> PingAsync(string conn)
{
    try
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1";
        _ = await cmd.ExecuteScalarAsync();
        return (true, null);
    }
    catch (Exception ex)
    {
        return (false, ex.GetType().Name + ": " + ex.Message);
    }
}

public sealed record DbEndpoints(string Primary, string Replica);

// Exposed for WebApplicationFactory<Program> in the test project.
public partial class Program;
