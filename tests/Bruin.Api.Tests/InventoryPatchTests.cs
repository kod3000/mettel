using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Bruin.Api.Tests;

// Full-field PATCH /inventory/{id} — B1 endpoint. Covers happy path,
// optimistic concurrency, and field_policy admin-only field enforcement.
[Collection(PostgresCollection.Name)]
public sealed class InventoryPatchTests
{
    private readonly PostgresFixture _fx;
    public InventoryPatchTests(PostgresFixture fx) => _fx = fx;

    private HttpClient Client(string apiKey)
    {
        var c = _fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return c;
    }

    private async Task<(Guid id, int rowVersion)> CreateAsAdmin(string sn)
    {
        var admin = Client(PostgresFixture.ApiKeyA_Admin);
        using var res = await admin.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = sn, productCategory = "voice",
            productName = "row", status = "pending",
        });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("id").GetGuid(),
                doc.RootElement.GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task Patch_notes_bumps_rowversion_and_persists()
    {
        var (id, rv) = await CreateAsAdmin("PATCH-HAPPY-" + Guid.NewGuid().ToString("N")[..8]);
        var admin = Client(PostgresFixture.ApiKeyA_Admin);

        using var res = await admin.PatchAsJsonAsync($"/api/v1/inventory/{id}", new
        {
            rowVersion = rv,
            notes = "patched by test",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("patched by test", body.RootElement.GetProperty("notes").GetString());
        Assert.Equal(rv + 1, body.RootElement.GetProperty("rowVersion").GetInt32());
    }

    [Fact]
    public async Task Patch_with_stale_rowversion_returns_409()
    {
        var (id, _) = await CreateAsAdmin("PATCH-STALE-" + Guid.NewGuid().ToString("N")[..8]);
        var admin = Client(PostgresFixture.ApiKeyA_Admin);

        using var res = await admin.PatchAsJsonAsync($"/api/v1/inventory/{id}", new
        {
            rowVersion = 999,   // definitely stale
            notes = "stale",
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("concurrency-conflict", body.RootElement.GetProperty("type").GetString() ?? "");
    }

    [Fact]
    public async Task Patch_status_in_body_returns_per_field_400()
    {
        var (id, rv) = await CreateAsAdmin("PATCH-STATUS-" + Guid.NewGuid().ToString("N")[..8]);
        var admin = Client(PostgresFixture.ApiKeyA_Admin);

        using var res = await admin.PatchAsJsonAsync($"/api/v1/inventory/{id}", new
        {
            rowVersion = rv,
            status = "active",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var errors = body.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("status", out var statusErrs));
        Assert.Contains("PATCH", statusErrs[0].GetString() ?? "");
    }

    [Fact]
    public async Task Worker_cannot_patch_admin_only_field()
    {
        try
        {
            await _fx.SetAdminOnlyFieldAsync(_fx.ClientA, "productCategory");
            var (id, rv) = await CreateAsAdmin("PATCH-POLICY-" + Guid.NewGuid().ToString("N")[..8]);
            var worker = Client(PostgresFixture.ApiKeyA_Worker);

            using var res = await worker.PatchAsJsonAsync($"/api/v1/inventory/{id}", new
            {
                rowVersion = rv,
                productCategory = "data",
            });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var errors = body.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty("productCategory", out var catErrs));
            Assert.Contains("Admin-only", catErrs[0].GetString() ?? "");
        }
        finally
        {
            await _fx.ClearFieldPoliciesAsync(_fx.ClientA);
        }
    }

    [Fact]
    public async Task Admin_bypasses_field_policy()
    {
        try
        {
            await _fx.SetAdminOnlyFieldAsync(_fx.ClientA, "productCategory");
            var (id, rv) = await CreateAsAdmin("PATCH-BYPASS-" + Guid.NewGuid().ToString("N")[..8]);
            var admin = Client(PostgresFixture.ApiKeyA_Admin);

            using var res = await admin.PatchAsJsonAsync($"/api/v1/inventory/{id}", new
            {
                rowVersion = rv,
                productCategory = "data",
            });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal("data", body.RootElement.GetProperty("productCategory").GetString());
        }
        finally
        {
            await _fx.ClearFieldPoliciesAsync(_fx.ClientA);
        }
    }
}
