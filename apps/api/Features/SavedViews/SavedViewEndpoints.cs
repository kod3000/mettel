using System.Text.Json;
using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Bruin.Api.Features.Tenancy;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bruin.Api.Features.SavedViews;

// GET|POST /api/v1/saved-views, GET|PUT|DELETE /api/v1/saved-views/{id}.
// All tenant-scoped — cross-tenant get returns 404 like the rest of the API.
// Body is opaque JSON (filters/sort/columns.visible/order) — the UI owns
// the shape, the API just persists it.
public static class SavedViewEndpoints
{
    public static void MapSavedViews(this IEndpointRouteBuilder r)
    {
        r.MapGet("/api/v1/saved-views", ListAsync)
            .Produces<Contracts.SavedViewList>().ProducesProblem(401);
        r.MapGet("/api/v1/saved-views/{id:guid}", GetAsync)
            .Produces<Contracts.SavedViewResponse>().ProducesProblem(404);
        r.MapPost("/api/v1/saved-views", CreateAsync)
            .RequireRole(Roles.Admin, Roles.Worker)
            .Produces<Contracts.SavedViewResponse>(StatusCodes.Status201Created)
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(409);
        r.MapPut("/api/v1/saved-views/{id:guid}", UpdateAsync)
            .RequireRole(Roles.Admin, Roles.Worker)
            .Produces<Contracts.SavedViewResponse>()
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404);
        r.MapDelete("/api/v1/saved-views/{id:guid}", DeleteAsync)
            .RequireRole(Roles.Admin, Roles.Worker)
            .ProducesProblem(403).ProducesProblem(404);
    }

    private static async Task<IResult> ListAsync(ITenantContext t, IReadRouter db, CancellationToken ct)
    {
        if (t.ClientId is not Guid clientId) return Problem.Unauthorized();
        await using var conn = await db.OpenReadAsync(ct);
        var rows = (await conn.QueryAsync<SavedViewDto>(new CommandDefinition(@"
            SELECT id, name, filters::text AS filters, sort::text AS sort, columns::text AS columns,
                   created_at, updated_at
            FROM public.saved_view
            WHERE client_id = @clientId
            ORDER BY updated_at DESC",
            new { clientId }, cancellationToken: ct))).ToArray();
        return Results.Ok(new Contracts.SavedViewList(rows.Select(ToWire).ToArray()));
    }

    private static async Task<IResult> GetAsync(Guid id, ITenantContext t, IReadRouter db, CancellationToken ct)
    {
        if (t.ClientId is not Guid clientId) return Problem.Unauthorized();
        await using var conn = await db.OpenReadAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<SavedViewDto>(new CommandDefinition(@"
            SELECT id, name, filters::text AS filters, sort::text AS sort, columns::text AS columns,
                   created_at, updated_at
            FROM public.saved_view
            WHERE id = @id AND client_id = @clientId",
            new { id, clientId }, cancellationToken: ct));
        return row is null ? Problem.NotFound() : Results.Ok(ToWire(row));
    }

    private static async Task<IResult> CreateAsync(
        Contracts.SavedViewUpsert body, ITenantContext t, IDbConnections db,
        ILsnContext lsn, CancellationToken ct)
    {
        if (t.ClientId is not Guid clientId) return Problem.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Name))
            return Problem.ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Required."] });

        var id = Guid.CreateVersion7();
        await using var conn = await db.OpenPrimaryAsync(ct);
        try
        {
            var (row, writeLsn) = await UpsertReturning(conn,
                @"INSERT INTO public.saved_view (id, client_id, name, filters, sort, columns)
                  VALUES (@id, @clientId, @name, @filters::jsonb, @sort::jsonb, @columns::jsonb)
                  RETURNING id, name, filters::text AS filters, sort::text AS sort, columns::text AS columns,
                            created_at, updated_at, pg_current_wal_lsn()::text AS write_lsn",
                new
                {
                    id, clientId, name = body.Name.Trim(),
                    filters = body.Filters ?? "{}",
                    sort = body.Sort ?? "{}",
                    columns = body.Columns ?? "{}",
                }, ct);
            lsn.RecordWrite(writeLsn);
            return Results.Created($"/api/v1/saved-views/{row.id}", ToWire(row));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            return Problem.Conflict(ErrorSlugs.ValidationFailed, "Duplicate saved-view name",
                "A saved view with this name already exists for this tenant.");
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, Contracts.SavedViewUpsert body, ITenantContext t,
        IDbConnections db, ILsnContext lsn, CancellationToken ct)
    {
        if (t.ClientId is not Guid clientId) return Problem.Unauthorized();
        if (string.IsNullOrWhiteSpace(body.Name))
            return Problem.ValidationFailed(new Dictionary<string, string[]> { ["name"] = ["Required."] });

        await using var conn = await db.OpenPrimaryAsync(ct);
        SavedViewDto? row;
        string writeLsn;
        try
        {
            var result = await UpsertReturning(conn,
                @"UPDATE public.saved_view
                    SET name = @name,
                        filters = @filters::jsonb,
                        sort = @sort::jsonb,
                        columns = @columns::jsonb,
                        updated_at = now()
                    WHERE id = @id AND client_id = @clientId
                    RETURNING id, name, filters::text AS filters, sort::text AS sort, columns::text AS columns,
                              created_at, updated_at, pg_current_wal_lsn()::text AS write_lsn",
                new
                {
                    id, clientId, name = body.Name.Trim(),
                    filters = body.Filters ?? "{}",
                    sort = body.Sort ?? "{}",
                    columns = body.Columns ?? "{}",
                }, ct);
            row = result.row;
            writeLsn = result.writeLsn;
        }
        catch (InvalidOperationException)
        {
            return Problem.NotFound();
        }
        lsn.RecordWrite(writeLsn);
        return Results.Ok(ToWire(row!));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, ITenantContext t, IDbConnections db, ILsnContext lsn, CancellationToken ct)
    {
        if (t.ClientId is not Guid clientId) return Problem.Unauthorized();
        await using var conn = await db.OpenPrimaryAsync(ct);
        var writeLsn = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(@"
            WITH d AS (
                DELETE FROM public.saved_view
                WHERE id = @id AND client_id = @clientId
                RETURNING 1
            )
            SELECT CASE WHEN EXISTS(SELECT 1 FROM d) THEN pg_current_wal_lsn()::text ELSE NULL END",
            new { id, clientId }, cancellationToken: ct));
        if (writeLsn is null) return Problem.NotFound();
        lsn.RecordWrite(writeLsn);
        return Results.NoContent();
    }

    // Dapper + jsonb: pass string via @param::jsonb cast. Returning both the
    // updated row and the LSN keeps this to one round trip.
    private static async Task<(SavedViewDto row, string writeLsn)> UpsertReturning(
        Npgsql.NpgsqlConnection conn, string sql, object p, CancellationToken ct)
    {
        var result = await conn.QuerySingleAsync<SavedViewDtoWithLsn>(
            new CommandDefinition(sql, p, cancellationToken: ct));
        return (result, result.write_lsn);
    }

    // ---- shapes ------------------------------------------------------

    private class SavedViewDto
    {
        public Guid id { get; set; }
        public string name { get; set; } = "";
        public string filters { get; set; } = "{}";
        public string sort { get; set; } = "{}";
        public string columns { get; set; } = "{}";
        public DateTimeOffset created_at { get; set; }
        public DateTimeOffset updated_at { get; set; }
    }

    private sealed class SavedViewDtoWithLsn : SavedViewDto
    {
        public string write_lsn { get; set; } = "";
    }

    private static Contracts.SavedViewResponse ToWire(SavedViewDto r) => new(
        Id: r.id,
        Name: r.name,
        Filters: JsonDocument.Parse(r.filters).RootElement.Clone(),
        Sort:    JsonDocument.Parse(r.sort).RootElement.Clone(),
        Columns: JsonDocument.Parse(r.columns).RootElement.Clone(),
        CreatedAt: r.created_at,
        UpdatedAt: r.updated_at);
}
