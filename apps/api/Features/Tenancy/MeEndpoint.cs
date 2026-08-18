using Bruin.Api.Contracts;
using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Dapper;

namespace Bruin.Api.Features.Tenancy;

// GET /api/v1/me — the SPA fetches this once on mount to decide which
// UI actions to show. Cheap: reads only field_policy (no join back to
// api_key — the caller's role is already on the ITenantContext).
//
// Response:
//   {
//     "clientId": "uuid",
//     "role": "admin" | "worker" | "reader",
//     "adminOnlyFields": [ "field_name", ... ]   // per-tenant policy
//   }
//
// Empty adminOnlyFields for an admin (they can write everything) or when
// no field_policy rows exist (permissive default — workers can write all).
public static class MeEndpoint
{
    public static void MapMe(this IEndpointRouteBuilder r)
    {
        r.MapGet("/api/v1/me", GetMeAsync)
            .WithName("GetMe")
            .Produces<MeResponse>()
            .ProducesProblem(401);
    }

    private static async Task<IResult> GetMeAsync(
        ITenantContext tenant, IDbConnections db, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId || tenant.Role is not string role)
            return Problem.Unauthorized();

        await using var conn = await db.OpenReplicaAsync(ct);

        // One round-trip for both the tenant display name (used by the
        // SPA's custom-key chip) and the admin-only field list. UNION
        // ALL keeps the query flat and lets the connection stream two
        // shapes back without a second command.
        var name = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT name FROM public.client WHERE id = @cid",
            new { cid = clientId }, cancellationToken: ct)) ?? "";

        var adminOnly = (await conn.QueryAsync<string>(new CommandDefinition(@"
            SELECT field_name
            FROM public.field_policy
            WHERE client_id = @cid AND min_role = 'admin'
            ORDER BY field_name",
            new { cid = clientId }, cancellationToken: ct))).ToArray();

        return Results.Ok(new MeResponse(clientId, name, role, adminOnly));
    }
}
