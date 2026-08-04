using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Bruin.Api.Tests;

// Four gate tests, in the order the build plan asks for them:
//   (a) full paginate: pageSize=100 yields every row exactly once
//   (b) tenant A's cursor rejected under tenant B's key   → 400 cursor-invalid
//   (c) filter change mid-pagination                       → 400 cursor-stale
//   (d) concurrent insert never skips pre-existing rows
[Collection(PostgresCollection.Name)]
public sealed class ListEndpointTests
{
    private readonly PostgresFixture _fx;
    public ListEndpointTests(PostgresFixture fx) => _fx = fx;

    private HttpClient Client(string apiKey)
    {
        var c = _fx.Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return c;
    }

    // ---------------------------------------------------------------- (a)
    [Fact]
    public async Task Paginating_full_result_set_yields_every_row_exactly_once()
    {
        await _fx.TruncateInventoryAsync();
        const int total = 550;
        await _fx.SeedInventoryAsync(_fx.ClientA, total, seed: 100);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var client = Client(PostgresFixture.ApiKeyA);
        string? cursor = null;
        int pages = 0;
        while (true)
        {
            pages++;
            Assert.True(pages < 100, "pagination did not terminate — likely a keyset bug");
            var url = $"/api/v1/inventory?pageSize=100" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var res = await client.GetAsync(url);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var rows = doc.RootElement.GetProperty("rows");
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.GetProperty("id").GetString()!;
                Assert.True(seen.Add(id), $"duplicate row across pages: {id}");
            }
            var hasMore = doc.RootElement.GetProperty("hasMore").GetBoolean();
            cursor = doc.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : doc.RootElement.GetProperty("nextCursor").GetString();
            if (!hasMore) break;
        }
        Assert.Equal(total, seen.Count);
    }

    // ---------------------------------------------------------------- (b)
    [Fact]
    public async Task Cursor_issued_for_tenant_A_is_rejected_under_tenant_B()
    {
        await _fx.TruncateInventoryAsync();
        await _fx.SeedInventoryAsync(_fx.ClientA, 250, seed: 200);
        await _fx.SeedInventoryAsync(_fx.ClientB, 100, seed: 300);

        var a = Client(PostgresFixture.ApiKeyA);
        using var page1 = await a.GetAsync("/api/v1/inventory?pageSize=50");
        page1.EnsureSuccessStatusCode();
        using var d1 = JsonDocument.Parse(await page1.Content.ReadAsStringAsync());
        var cursor = d1.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var b = Client(PostgresFixture.ApiKeyB);
        using var crossed = await b.GetAsync($"/api/v1/inventory?pageSize=50&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, crossed.StatusCode);
        using var problem = JsonDocument.Parse(await crossed.Content.ReadAsStringAsync());
        var type = problem.RootElement.GetProperty("type").GetString();
        Assert.EndsWith("cursor-invalid", type);
    }

    // ---------------------------------------------------------------- (c)
    [Fact]
    public async Task Changing_filter_mid_pagination_yields_cursor_stale()
    {
        await _fx.TruncateInventoryAsync();
        await _fx.SeedInventoryAsync(_fx.ClientA, 500, seed: 400);

        var client = Client(PostgresFixture.ApiKeyA);
        using var page1 = await client.GetAsync("/api/v1/inventory?pageSize=50&status=active");
        page1.EnsureSuccessStatusCode();
        using var d1 = JsonDocument.Parse(await page1.Content.ReadAsStringAsync());
        var cursor = d1.RootElement.GetProperty("nextCursor").GetString();

        // Same cursor but a different filter set — must be rejected.
        using var page2 = await client.GetAsync($"/api/v1/inventory?pageSize=50&status=pending&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, page2.StatusCode);
        using var problem = JsonDocument.Parse(await page2.Content.ReadAsStringAsync());
        var type = problem.RootElement.GetProperty("type").GetString();
        Assert.EndsWith("cursor-stale", type);
    }

    // ---------------------------------------------------------------- (d)
    [Fact]
    public async Task Concurrent_inserts_do_not_skip_pre_existing_rows()
    {
        await _fx.TruncateInventoryAsync();
        // Older rows: created_at strictly before the pagination starts.
        var start = DateTimeOffset.UtcNow.AddDays(-90);
        await _fx.SeedInventoryAsync(_fx.ClientA, 300, seed: 500, baseTime: start);

        var pre = await CollectAllRows(_fx.ClientA, pageSize: 25, midPageAction: async pageNum =>
        {
            // Between pages 3 and 4, insert brand-new rows in the *future*
            // (relative to our sort direction, descending by created_at, that
            // means these newcomers should appear on subsequent pages — never
            // as replacements for pre-existing rows).
            if (pageNum == 3)
            {
                await using var c = new NpgsqlConnection(_fx.ConnString);
                await c.OpenAsync();
                using var writer = c.BeginBinaryImport(@"
                    COPY public.inventory
                    (id, client_id, service_number, product_category, product_name, status,
                     city, state, address, assignee, notes, created_at, updated_at, row_version)
                    FROM STDIN (FORMAT BINARY)");
                var future = DateTimeOffset.UtcNow.AddDays(30);
                for (int i = 0; i < 50; i++)
                {
                    var at = future.AddSeconds(i);
                    writer.StartRow();
                    writer.Write(Guid.CreateVersion7(at), NpgsqlDbType.Uuid);
                    writer.Write(_fx.ClientA, NpgsqlDbType.Uuid);
                    writer.Write($"999-CON-{i:D6}", NpgsqlDbType.Varchar);
                    writer.Write("other", NpgsqlDbType.Varchar);
                    writer.Write($"Concurrent #{i}", NpgsqlDbType.Varchar);
                    writer.Write("active", NpgsqlDbType.Varchar);
                    writer.Write("Test City", NpgsqlDbType.Varchar);
                    writer.Write("NY", NpgsqlDbType.Varchar);
                    writer.Write($"CON-{i}", NpgsqlDbType.Varchar);
                    writer.WriteNull();
                    writer.WriteNull();
                    writer.Write(at, NpgsqlDbType.TimestampTz);
                    writer.Write(at, NpgsqlDbType.TimestampTz);
                    writer.Write(1, NpgsqlDbType.Integer);
                }
                writer.Complete();
            }
        });

        // All 300 pre-existing rows must be present. The 50 newcomers may or
        // may not appear (depends on which page the keyset had reached), but
        // the pre-existing set must survive intact.
        var preIds = pre.Where(id => !id.serviceNumber.StartsWith("999-CON-", StringComparison.Ordinal)).ToList();
        Assert.Equal(300, preIds.Count);
    }

    // ---- helpers ---------------------------------------------------------

    private async Task<List<(string id, string serviceNumber)>> CollectAllRows(
        Guid _,
        int pageSize,
        Func<int, Task>? midPageAction = null)
    {
        var client = Client(PostgresFixture.ApiKeyA);
        var rows = new List<(string, string)>();
        string? cursor = null;
        int pageNum = 0;
        while (true)
        {
            pageNum++;
            var url = $"/api/v1/inventory?pageSize={pageSize}" +
                      (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var res = await client.GetAsync(url);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
                rows.Add((row.GetProperty("id").GetString()!, row.GetProperty("serviceNumber").GetString()!));
            if (midPageAction is not null) await midPageAction(pageNum);
            var hasMore = doc.RootElement.GetProperty("hasMore").GetBoolean();
            cursor = doc.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : doc.RootElement.GetProperty("nextCursor").GetString();
            if (!hasMore) break;
            Assert.True(pageNum < 200, "runaway pagination");
        }
        return rows;
    }
}
