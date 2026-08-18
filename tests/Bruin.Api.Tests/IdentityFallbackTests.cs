using System.Net;
using Bruin.Api.Features.Tenancy;
using Dapper;
using Npgsql;
using Xunit;

namespace Bruin.Api.Tests;

// Exercises the ApiKeyResolverWithFallback wrapper end-to-end via
// WebApplicationFactory. The stub identity resolver lives on the
// fixture so any collection member can script responses.
[Collection(PostgresCollection.Name)]
public sealed class IdentityFallbackTests
{
    private readonly PostgresFixture _fx;

    public IdentityFallbackTests(PostgresFixture fx)
    {
        _fx = fx;
        // Every test starts with a clean stub — leftover scripted
        // responses from another test file would poison assertions.
        _fx.IdentityStub.Reset();
    }

    [Fact]
    public async Task Local_key_bypasses_identity_fallback()
    {
        // Sanity: a key that the local api_key table already knows must
        // not touch identity at all.
        _fx.IdentityStub.Reset();
        using var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PostgresFixture.ApiKeyA_Admin);

        using var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(0, _fx.IdentityStub.CallCount);
    }

    [Fact]
    public async Task Unknown_key_with_identity_hit_binds_the_returned_tenant()
    {
        var tenantId = Guid.CreateVersion7();
        const string tenantName = "Fallback Tenant Alpha";
        const string key = "kbrk_fallback_alpha_1234567890";

        _fx.IdentityStub.SetResponse(key, new IdentityResolution(tenantId, tenantName, "admin"));

        using var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        using var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(tenantId, doc.RootElement.GetProperty("clientId").GetGuid());
        Assert.Equal("admin", doc.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Identity_resolved_tenant_gets_a_local_client_row()
    {
        var tenantId = Guid.CreateVersion7();
        const string tenantName = "Fallback Tenant Beta";
        const string key = "kbrk_fallback_beta_1234567890";

        _fx.IdentityStub.SetResponse(key, new IdentityResolution(tenantId, tenantName, "worker"));

        using var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        (await client.GetAsync("/api/v1/me")).EnsureSuccessStatusCode();

        // The wrapper must have upserted a client row so any subsequent
        // inventory query — which FK-checks against client.id — works.
        await using var conn = new NpgsqlConnection(_fx.ConnString);
        await conn.OpenAsync();
        var row = await conn.QuerySingleOrDefaultAsync<(Guid Id, string Name)?>(
            "SELECT id AS Id, name AS Name FROM public.client WHERE id = @id",
            new { id = tenantId });
        Assert.NotNull(row);
        Assert.Equal(tenantId, row.Value.Id);
        Assert.Equal(tenantName, row.Value.Name);
    }

    [Fact]
    public async Task Identity_401_returns_401_to_caller()
    {
        const string key = "kbrk_unknown_at_identity_too";
        // Default (no SetResponse) already returns null → same as identity 401.

        using var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        using var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Identity_resolved_tenant_sees_empty_inventory_end_to_end()
    {
        // Proves the full pipeline: identity resolves → client row
        // materialised → the /api/v1/inventory endpoint accepts the
        // request and its client_id WHERE filter returns the tenant's
        // (empty) slice without leaking any other tenant's rows.
        var tenantId = Guid.CreateVersion7();
        const string key = "kbrk_isolated_gamma_1234567890";
        _fx.IdentityStub.SetResponse(key, new IdentityResolution(tenantId, "Gamma", "admin"));

        using var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        using var list = await client.GetAsync("/api/v1/inventory?limit=10");
        list.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(0, rows.GetArrayLength());
    }
}
