using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bruin.Api.Data;
using Dapper;

namespace Bruin.Api.Features.Tenancy;

// Identity fallback: when a raw X-Api-Key doesn't match any row in the
// local api_key table, ask mt-oidc's /resolve endpoint. On 200 we bind
// the caller as (tenantId, role) — exactly the same shape the local
// path produces — and lazily materialise a local `client` row for the
// tenant so downstream inventory queries pass their FK check.
//
// mt-oidc's roles are the same three strings the grid already enforces
// (admin / worker / reader), so no mapping is needed. The identity
// service is authoritative for revocations, so we cache with a short
// TTL rather than the local resolver's unbounded lifetime.

public sealed record IdentityResolution(Guid TenantId, string TenantName, string Role);

public interface IIdentityResolver
{
    Task<IdentityResolution?> ResolveAsync(string apiKey, CancellationToken ct);
}

public sealed class HttpIdentityResolver : IIdentityResolver
{
    // 60 s: long enough to absorb burst traffic, short enough that a
    // key revoked at identity flushes from every grid instance within
    // a minute without paging the operator.
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ILogger<HttpIdentityResolver> _log;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public HttpIdentityResolver(HttpClient http, ILogger<HttpIdentityResolver> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<IdentityResolution?> ResolveAsync(string apiKey, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(apiKey, out var hit) && hit.ExpiresAt > now)
            return hit.Resolution;

        try
        {
            // /resolve accepts the key via header OR body. Header keeps
            // the request body empty so this call is a tiny fixed-size POST.
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/resolve");
            req.Headers.Add("X-Api-Key", apiKey);
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Cache the 401 too — a brute-force scan of bad keys must
                // not turn into a brute-force scan of our identity service.
                _cache[apiKey] = new CacheEntry(now.Add(CacheTtl), null);
                return null;
            }
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning("identity /resolve returned {Status} for key prefix {Prefix}",
                    (int)res.StatusCode, apiKey.Length >= 8 ? apiKey[..8] : "?");
                return null; // transient — don't cache
            }

            var body = await res.Content.ReadFromJsonAsync<ResolvePayload>(cancellationToken: ct);
            if (body is null || body.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(body.Role))
            {
                _log.LogWarning("identity /resolve returned unexpected body");
                return null;
            }

            var resolution = new IdentityResolution(body.TenantId, body.TenantName ?? "", body.Role);
            _cache[apiKey] = new CacheEntry(now.Add(CacheTtl), resolution);
            return resolution;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                or TaskCanceledException
                                or OperationCanceledException)
        {
            _log.LogWarning(ex, "identity /resolve failed for key prefix {Prefix}",
                apiKey.Length >= 8 ? apiKey[..8] : "?");
            return null; // fail safe — never accept a key we couldn't verify
        }
    }

    private sealed record CacheEntry(DateTimeOffset ExpiresAt, IdentityResolution? Resolution);

    // Matches mt-oidc's Contracts/Wire.cs ResolveResponse. Only the two
    // fields the grid cares about are strictly needed; the rest come
    // along for logging / future use.
    private sealed record ResolvePayload(
        Guid TenantId,
        string? TenantName,
        string? TenantSlug,
        string Role,
        Guid KeyId,
        string? Label);
}

// Decorator around ApiKeyResolver. Local lookup is tried first (fast
// path, unbounded cache). On miss we ask identity, and on a hit there
// we upsert the tenant into the local `client` table so inventory FKs
// don't reject the first request from a brand-new tenant.
public sealed class ApiKeyResolverWithFallback : IApiKeyResolver
{
    private readonly ApiKeyResolver _local;
    private readonly IIdentityResolver _identity;
    private readonly IDbConnections _db;
    private readonly ILogger<ApiKeyResolverWithFallback> _log;

    public ApiKeyResolverWithFallback(
        ApiKeyResolver local,
        IIdentityResolver identity,
        IDbConnections db,
        ILogger<ApiKeyResolverWithFallback> log)
    {
        _local = local;
        _identity = identity;
        _db = db;
        _log = log;
    }

    public async Task<ResolvedKey?> ResolveAsync(string apiKey, CancellationToken ct)
    {
        var local = await _local.ResolveAsync(apiKey, ct);
        if (local is not null) return local;

        var external = await _identity.ResolveAsync(apiKey, ct);
        if (external is null) return null;

        await EnsureClientRowAsync(external.TenantId, external.TenantName, ct);
        return new ResolvedKey(external.TenantId, external.Role);
    }

    // Idempotent insert. `client.api_key` is UNIQUE and NOT NULL, so we
    // synthesise a per-tenant marker (`identity:<guid>`) — it never
    // collides with a real caller key, and gives an operator scanning
    // the client table a clear signal of which rows were materialised
    // from mt-oidc rather than seeded locally. The authoritative key
    // check still runs through /resolve on every cache miss.
    private async Task EnsureClientRowAsync(Guid tenantId, string name, CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenPrimaryAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO public.client (id, name, api_key, created_at)
                  VALUES (@id, @name, @marker, now())
                  ON CONFLICT (id) DO NOTHING",
                new
                {
                    id = tenantId,
                    name = string.IsNullOrWhiteSpace(name) ? "identity tenant" : name,
                    marker = $"identity:{tenantId:D}",
                },
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            // If we can't upsert, the caller will still get a ResolvedKey
            // and the FK will fail loudly on the next inventory query.
            // Log LOUDLY (Error, not Warning) so an operator sees it in
            // production rather than the request silently 500-ing later.
            _log.LogError(ex, "failed to materialise client row for tenant {TenantId} (name={Name})", tenantId, name);
        }
    }
}
