using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bruin.Api.Features.Inventory;

// The *only* place in the API where OFFSET appears — every other list path
// uses keyset pagination.
// Kept behind BRUIN_BENCH_MODE=1 so it can't accidentally ship to prod;
// exists solely so `bench/grid.js` can compare keyset vs OFFSET latencies at
// depth against the same seeded dataset.
public static class BenchOffsetEndpoint
{
    public static void MapBenchOffset(this IEndpointRouteBuilder r, bool enabled)
    {
        if (!enabled) return;
        r.MapGet("/bench/offset", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        ITenantContext tenant,
        IReadRouter db,
        CancellationToken ct,
        int depth = 0,
        int pageSize = 100)
    {
        if (tenant.ClientId is not Guid clientId) return Problem.Unauthorized();
        depth = Math.Max(0, depth);
        pageSize = Math.Clamp(pageSize, 1, 200);

        const string cols = "id, service_number, product_category, product_name, status, " +
                            "city, state, address, assignee, notes, created_at, updated_at, row_version";
        // Deliberately naïve: the whole point is to show why we don't do this.
        var sql = $@"
            SELECT {cols}
            FROM public.inventory
            WHERE client_id = @clientId
            ORDER BY created_at DESC, id DESC
            OFFSET @depth LIMIT @take";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var conn = await db.OpenReadAsync(ct);
        var rows = await conn.QueryAsync<InventoryRowDto>(new CommandDefinition(
            sql, new { clientId, depth, take = pageSize }, cancellationToken: ct));
        return Results.Json(new { rows = rows.ToArray(), tookMs = sw.ElapsedMilliseconds });
    }
}
