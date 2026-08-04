using System.Collections.Concurrent;
using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Dapper;
using Npgsql;

namespace Bruin.Api.Features.Tenancy;

// Simple X-Api-Key → client mapping. The exercise says header auth is fine,
// so no real IdP; the trade-off is documented in the design doc.
// Cache is process-local and refreshed lazily on cache miss — with 3 seeded
// clients it doesn't matter, but the interface won't need to change if that
// grows into hundreds.
public interface IApiKeyResolver
{
    Task<Guid?> ResolveAsync(string apiKey, CancellationToken ct);
}

public sealed class ApiKeyResolver(IDbConnections db) : IApiKeyResolver
{
    private static readonly ConcurrentDictionary<string, Guid> _cache = new(StringComparer.Ordinal);

    public async Task<Guid?> ResolveAsync(string apiKey, CancellationToken ct)
    {
        if (_cache.TryGetValue(apiKey, out var cached)) return cached;

        // Client rows are effectively write-once (seeded); the process-local
        // cache above absorbs the load. Try replica first so cold-start
        // auth traffic doesn't burden the primary; fall back to primary
        // on any replica failure (paused replica must NOT block auth —
        // this is the /health/ready → 503 window from ADR-0003).
        var id = await TryLookup(await OpenAsync(preferReplica: true, ct), apiKey, ct);
        id ??= await TryLookup(await OpenAsync(preferReplica: false, ct), apiKey, ct);
        if (id is Guid g && g != Guid.Empty)
        {
            _cache[apiKey] = g;
            return g;
        }
        return null;
    }

    private async ValueTask<NpgsqlConnection?> OpenAsync(bool preferReplica, CancellationToken ct)
    {
        try { return await (preferReplica ? db.OpenReplicaAsync(ct) : db.OpenPrimaryAsync(ct)); }
        catch (Exception ex) when (ex is NpgsqlException
                                || ex is System.Net.Sockets.SocketException
                                || ex is System.TimeoutException
                                || ex is System.IO.IOException) { return null; }
    }

    private static async Task<Guid?> TryLookup(NpgsqlConnection? conn, string apiKey, CancellationToken ct)
    {
        if (conn is null) return null;
        try
        {
            return await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM public.client WHERE api_key = @k LIMIT 1",
                new { k = apiKey }, cancellationToken: ct));
        }
        catch (Exception ex) when (ex is NpgsqlException
                                || ex is System.Net.Sockets.SocketException
                                || ex is System.TimeoutException
                                || ex is System.IO.IOException) { return null; }
        finally { await conn.DisposeAsync(); }
    }
}

// Middleware order matters: this must run *after* endpoints are matched so
// /health/* can stay open, but before the endpoint executes. We special-case
// public paths inline rather than layering yet another middleware.
public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, IApiKeyResolver resolver, ITenantContext tenant)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (IsPublic(path))
        {
            await next(ctx);
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            await WriteUnauthorized(ctx, "Missing X-Api-Key header.");
            return;
        }

        var clientId = await resolver.ResolveAsync(raw.ToString(), ctx.RequestAborted);
        if (clientId is null)
        {
            // Deliberately vague — do not leak whether a given key existed.
            await WriteUnauthorized(ctx, "Unknown or invalid API key.");
            return;
        }

        tenant.Set(clientId.Value);
        await next(ctx);
    }

    private static bool IsPublic(string path)
        => path == "/"
        || path.StartsWith("/health/", StringComparison.Ordinal)
        || path == "/metrics"
        || path.StartsWith("/openapi/", StringComparison.Ordinal);

    private static async Task WriteUnauthorized(HttpContext ctx, string detail)
    {
        var result = Problem.Unauthorized(detail);
        await result.ExecuteAsync(ctx);
    }
}
