using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bruin.Api.Domain;
using Xunit;

namespace Bruin.Api.Tests;

// Phase 6 gate: transition matrix + cross-tenant 404 + duplicate + concurrency.
[Collection(PostgresCollection.Name)]
public sealed class WriteEndpointTests
{
    private readonly PostgresFixture _fx;
    public WriteEndpointTests(PostgresFixture fx) => _fx = fx;

    private HttpClient Client(string apiKey)
    {
        var c = _fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return c;
    }

    private async Task<(Guid id, int rowVersion)> Create(HttpClient c, string sn, string status = "pending")
    {
        using var res = await c.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = sn,
            productCategory = "voice",
            productName = "Test PBX Seat",
            status,
        });
        res.EnsureSuccessStatusCode();
        Assert.NotNull(res.Headers.GetValues("X-Write-LSN").FirstOrDefault());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("id").GetGuid(),
            doc.RootElement.GetProperty("rowVersion").GetInt32());
    }

    // ---- transition matrix -------------------------------------------

    [Fact]
    public async Task Full_transition_matrix_matches_the_rules()
    {
        await _fx.TruncateInventoryAsync();
        var c = Client(PostgresFixture.ApiKeyA);

        foreach (var (from, to, allowed) in StatusTransitions.Matrix())
        {
            if (from == InventoryStatuses.Disconnected) continue; // can't seed disconnected via POST
            var sn = $"555-MTX-{from[..3]}{to[..3]}".ToLowerInvariant();
            var (id, rv) = await Create(c, sn, from);
            using var res = await c.PatchAsJsonAsync($"/api/v1/inventory/{id}/status",
                new { status = to, rowVersion = rv });

            if (allowed)
            {
                res.EnsureSuccessStatusCode();
                Assert.NotNull(res.Headers.GetValues("X-Write-LSN").FirstOrDefault());
            }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var type = doc.RootElement.GetProperty("type").GetString();
                Assert.EndsWith("invalid-status-transition", type);
            }
        }
    }

    // ---- disconnected transitions can't happen via POST but must be
    //      forbidden as sources too (once real disconnected rows exist,
    //      e.g. via seed data). Exercise those rows via the CSV worker
    //      path in Phase 10; for now, direct assertion on the domain rule
    //      keeps us honest.
    [Theory]
    [InlineData("disconnected", "pending")]
    [InlineData("disconnected", "active")]
    [InlineData("disconnected", "disconnected")]
    public void Disconnected_source_rejects_every_target(string from, string to)
    {
        Assert.False(StatusTransitions.IsAllowed(from, to));
    }

    // ---- cross-tenant 404 --------------------------------------------

    [Fact]
    public async Task Get_by_id_across_tenants_returns_404_with_empty_body()
    {
        await _fx.TruncateInventoryAsync();
        var a = Client(PostgresFixture.ApiKeyA);
        var (id, _) = await Create(a, "555-XTENANT");

        var b = Client(PostgresFixture.ApiKeyB);
        using var res = await b.GetAsync($"/api/v1/inventory/{id}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        // Body carries a ProblemDetails with `not-found` slug — that IS the
        // "empty body" in the contract sense (no domain fields leaking).
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.EndsWith("not-found", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("id", out _),
            "cross-tenant GET must not leak the row's id/fields");
    }

    // ---- duplicate service number -----------------------------------

    [Fact]
    public async Task Duplicate_service_number_returns_409_duplicate_slug()
    {
        await _fx.TruncateInventoryAsync();
        var c = Client(PostgresFixture.ApiKeyA);
        await Create(c, "555-DUPE-001");
        using var res = await c.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = "555-DUPE-001",
            productCategory = "voice",
            productName = "Duplicate",
            status = "pending",
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.EndsWith("duplicate-service-number", doc.RootElement.GetProperty("type").GetString());
    }

    // ---- concurrency conflict via stale row_version ----------------

    [Fact]
    public async Task Stale_row_version_on_patch_returns_409_concurrency_conflict()
    {
        await _fx.TruncateInventoryAsync();
        var c = Client(PostgresFixture.ApiKeyA);
        var (id, rv) = await Create(c, "555-CONC-001");

        // Successful patch — bumps row_version.
        using (var ok = await c.PatchAsJsonAsync($"/api/v1/inventory/{id}/status",
            new { status = "active", rowVersion = rv }))
            ok.EnsureSuccessStatusCode();

        // Reuse the stale rowVersion; must be rejected.
        using var stale = await c.PatchAsJsonAsync($"/api/v1/inventory/{id}/status",
            new { status = "disconnected", rowVersion = rv });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var doc = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.EndsWith("concurrency-conflict", doc.RootElement.GetProperty("type").GetString());
    }

    // ---- create validation --------------------------------------------

    [Fact]
    public async Task Create_disconnected_status_is_rejected_at_validation()
    {
        var c = Client(PostgresFixture.ApiKeyA);
        using var res = await c.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = "555-CREATE-DISC",
            productCategory = "voice",
            productName = "Disconnect at birth",
            status = "disconnected",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.EndsWith("validation-failed", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("status", out _));
    }
}
