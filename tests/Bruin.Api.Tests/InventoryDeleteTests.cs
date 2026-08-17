using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Bruin.Api.Tests;

// Soft delete + partial unique index — B2. Also covers the ON CONFLICT
// regression that broke every bulk upload once the unique index went
// partial: recreating a row after delete must not 500 on the writer.
[Collection(PostgresCollection.Name)]
public sealed class InventoryDeleteTests
{
    private readonly PostgresFixture _fx;
    public InventoryDeleteTests(PostgresFixture fx) => _fx = fx;

    private HttpClient Client(string apiKey)
    {
        var c = _fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return c;
    }

    private async Task<Guid> CreateAsAdmin(string sn)
    {
        var admin = Client(PostgresFixture.ApiKeyA_Admin);
        using var res = await admin.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = sn, productCategory = "voice",
            productName = "row to delete", status = "pending",
        });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Delete_then_GET_returns_404()
    {
        var id = await CreateAsAdmin("DEL-GET-" + Guid.NewGuid().ToString("N")[..8]);
        var admin = Client(PostgresFixture.ApiKeyA_Admin);

        using var del = await admin.DeleteAsync($"/api/v1/inventory/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        using var get = await admin.GetAsync($"/api/v1/inventory/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Delete_is_idempotent_returns_404_second_time()
    {
        var id = await CreateAsAdmin("DEL-IDEM-" + Guid.NewGuid().ToString("N")[..8]);
        var admin = Client(PostgresFixture.ApiKeyA_Admin);
        (await admin.DeleteAsync($"/api/v1/inventory/{id}")).EnsureSuccessStatusCode();

        using var second = await admin.DeleteAsync($"/api/v1/inventory/{id}");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // The whole reason the unique index went partial (B2) is so that a
    // soft-deleted service number can be reused. If this regresses, the
    // second POST would 409 duplicate-service-number.
    [Fact]
    public async Task Can_recreate_same_service_number_after_soft_delete()
    {
        var sn = "DEL-REUSE-" + Guid.NewGuid().ToString("N")[..8];
        var admin = Client(PostgresFixture.ApiKeyA_Admin);

        var firstId = await CreateAsAdmin(sn);
        (await admin.DeleteAsync($"/api/v1/inventory/{firstId}")).EnsureSuccessStatusCode();

        using var second = await admin.PostAsJsonAsync("/api/v1/inventory", new
        {
            serviceNumber = sn, productCategory = "data",
            productName = "reborn", status = "pending",
        });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondId = doc.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(firstId, secondId);
    }
}
