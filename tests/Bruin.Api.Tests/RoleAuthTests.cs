using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Bruin.Api.Tests;

// Role plumbing + RequireRole enforcement. All three roles resolve via
// /me; readers 403 on every mutation endpoint; workers 403 on delete only.
[Collection(PostgresCollection.Name)]
public sealed class RoleAuthTests
{
    private readonly PostgresFixture _fx;
    public RoleAuthTests(PostgresFixture fx) => _fx = fx;

    private HttpClient Client(string apiKey)
    {
        var c = _fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return c;
    }

    // ---- /me shape per role ------------------------------------------

    [Theory]
    [InlineData(PostgresFixture.ApiKeyA_Admin,  "admin")]
    [InlineData(PostgresFixture.ApiKeyA_Worker, "worker")]
    [InlineData(PostgresFixture.ApiKeyA_Reader, "reader")]
    public async Task Me_returns_role_from_api_key(string key, string expectedRole)
    {
        using var res = await Client(key).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(expectedRole, doc.RootElement.GetProperty("role").GetString());
        Assert.Equal(_fx.ClientA, doc.RootElement.GetProperty("clientId").GetGuid());
    }

    [Fact]
    public async Task Me_with_unknown_key_returns_401()
    {
        using var res = await Client("this-key-does-not-exist").GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- RequireRole gates on mutation endpoints ---------------------

    [Fact]
    public async Task Reader_cannot_POST_inventory()
    {
        using var res = await Client(PostgresFixture.ApiKeyA_Reader).PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = "ROLE-TEST-READER-POST",
            productCategory = "voice",
            productName = "should not stick",
            status = "pending",
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains("forbidden", doc.RootElement.GetProperty("type").GetString() ?? "");
    }

    [Fact]
    public async Task Reader_cannot_PATCH_status()
    {
        // Create as admin then attempt PATCH as reader.
        var admin = Client(PostgresFixture.ApiKeyA_Admin);
        using var create = await admin.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = "ROLE-TEST-READER-PATCH",
            productCategory = "voice", productName = "row", status = "pending",
        });
        create.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var res = await Client(PostgresFixture.ApiKeyA_Reader).PatchAsJsonAsync(
            $"/api/v1/inventory/{id}/status", new { status = "active", rowVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Worker_cannot_DELETE_inventory()
    {
        var admin = Client(PostgresFixture.ApiKeyA_Admin);
        using var create = await admin.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = "ROLE-TEST-WORKER-DEL",
            productCategory = "voice", productName = "row", status = "pending",
        });
        create.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var res = await Client(PostgresFixture.ApiKeyA_Worker).DeleteAsync($"/api/v1/inventory/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_can_DELETE_inventory()
    {
        var admin = Client(PostgresFixture.ApiKeyA_Admin);
        using var create = await admin.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = "ROLE-TEST-ADMIN-DEL",
            productCategory = "voice", productName = "row", status = "pending",
        });
        create.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var res = await admin.DeleteAsync($"/api/v1/inventory/{id}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }
}
