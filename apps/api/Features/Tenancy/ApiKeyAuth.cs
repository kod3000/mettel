using System.Collections.Concurrent;
using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Dapper;
using Npgsql;

namespace Bruin.Api.Features.Tenancy;

// X-Api-Key → (client, role) mapping. Reads from api_key (post-A1
// migration); falls back to client.api_key (legacy shape) if the key is
// missing there — the backfill should mean it never is, but the belt is
// cheap. Cache is process-local, keyed by raw API key.
public sealed record ResolvedKey(Guid ClientId, string Role);

public interface IApiKeyResolver
{
    Task<ResolvedKey?> ResolveAsync(string apiKey, CancellationToken ct);
}

public sealed class ApiKeyResolver(IDbConnections db) : IApiKeyResolver
{
    private static readonly ConcurrentDictionary<string, ResolvedKey> _cache = new(StringComparer.Ordinal);

    public async Task<ResolvedKey?> ResolveAsync(string apiKey, CancellationToken ct)
    {
        if (_cache.TryGetValue(apiKey, out var cached)) return cached;

        // Try replica first so cold-start auth traffic doesn't burden the
        // primary; fall back to primary on any replica failure (paused
        // replica must NOT block auth — see ADR-0003).
        var hit = await TryLookup(await OpenAsync(preferReplica: true, ct), apiKey, ct);
        hit ??= await TryLookup(await OpenAsync(preferReplica: false, ct), apiKey, ct);
        if (hit is not null)
        {
            _cache[apiKey] = hit;
            return hit;
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

    private static async Task<ResolvedKey?> TryLookup(NpgsqlConnection? conn, string apiKey, CancellationToken ct)
    {
        if (conn is null) return null;
        try
        {
            // Primary source: api_key (new table). LEFT JOIN'd against
            // client.api_key so we still resolve keys that somehow slipped
            // the A1 backfill (shouldn't happen — belt for the braces).
            return await conn.QuerySingleOrDefaultAsync<ResolvedKey?>(new CommandDefinition(@"
                SELECT client_id AS ClientId, role AS Role
                FROM public.api_key
                WHERE key = @k
                UNION ALL
                SELECT id AS ClientId, 'admin' AS Role
                FROM public.client
                WHERE api_key = @k
                  AND NOT EXISTS (SELECT 1 FROM public.api_key WHERE key = @k)
                LIMIT 1",
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

        var resolved = await resolver.ResolveAsync(raw.ToString(), ctx.RequestAborted);
        if (resolved is null)
        {
            // Deliberately vague — do not leak whether a given key existed.
            await WriteUnauthorized(ctx, "Unknown or invalid API key.");
            return;
        }

        tenant.Set(resolved.ClientId, resolved.Role);
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
