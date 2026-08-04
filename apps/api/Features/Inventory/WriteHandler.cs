using Bruin.Api.Contracts;
using Bruin.Api.Data;
using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Dapper;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Bruin.Api.Features.Inventory;

// Phase 6 write path — GET single, POST create, PATCH status.
//
// All three go through the primary via `IDbConnections.OpenPrimaryAsync` (we
// deliberately skip IReadRouter for GET-by-id too, because the caller of a
// GET-immediately-after-write is the one place we can't tolerate replica
// lag and the by-id lookup is cheap on primary). Mutations record their
// commit LSN into `ILsnContext` so the response filter stamps `X-Write-LSN`
// on the way out.
public sealed class WriteHandler(IDbConnections db, ILsnContext lsn)
{
    // ---- GET /inventory/{id} ------------------------------------------
    public async Task<IResult> GetAsync(Guid clientId, Guid id, CancellationToken ct)
    {
        const string cols = "id, service_number, product_category, product_name, status, " +
                            "city, state, address, assignee, notes, created_at, updated_at, row_version";
        const string sql = $@"
            SELECT {cols}
            FROM public.inventory
            WHERE id = @id AND client_id = @clientId";

        await using var conn = await db.OpenPrimaryAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<InventoryRowDto>(
            new CommandDefinition(sql, new { id, clientId }, cancellationToken: ct));
        // Contract: cross-tenant → 404, never 403 — a 403 would leak that the
        // row exists under some other tenant. Missing-id gets the same 404.
        return row is null ? Problem.NotFound() : Results.Ok(WireOf(row));
    }

    // ---- POST /inventory ----------------------------------------------
    public async Task<IResult> CreateAsync(Guid clientId, CreateRequest req, CancellationToken ct)
    {
        var fieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(req.ServiceNumber))
            fieldErrors["serviceNumber"] = ["Required."];
        if (!ProductCategories.All.Contains(req.ProductCategory ?? ""))
            fieldErrors["productCategory"] = [$"Must be one of {string.Join(", ", ProductCategories.All)}."];
        if (string.IsNullOrWhiteSpace(req.ProductName))
            fieldErrors["productName"] = ["Required."];
        var initialStatus = req.Status ?? InventoryStatuses.Pending;
        if (initialStatus != InventoryStatuses.Pending && initialStatus != InventoryStatuses.Active)
            fieldErrors["status"] = ["Must be `pending` or `active`. Creating disconnected inventory is not allowed."];
        if (fieldErrors.Count > 0) return Problem.ValidationFailed(fieldErrors);

        var id = Guid.CreateVersion7();
        const string sql = @"
            INSERT INTO public.inventory
            (id, client_id, service_number, product_category, product_name, status,
             city, state, address, assignee, notes)
            VALUES
            (@id, @clientId, @sn, @cat, @pn, @st, @city, @state, @addr, @asg, @notes)
            RETURNING id, service_number, product_category, product_name, status,
                      city, state, address, assignee, notes,
                      created_at, updated_at, row_version,
                      pg_current_wal_lsn()::text AS write_lsn";

        await using var conn = await db.OpenPrimaryAsync(ct);
        InventoryRowDto row;
        string writeLsn;
        try
        {
            var result = await conn.QuerySingleAsync<CreateRowWithLsn>(new CommandDefinition(sql, new
            {
                id,
                clientId,
                sn = req.ServiceNumber!.Trim(),
                cat = req.ProductCategory!,
                pn = req.ProductName!.Trim(),
                st = initialStatus,
                city = req.City,
                state = req.State,
                addr = req.Address,
                asg = req.Assignee,
                notes = req.Notes,
            }, cancellationToken: ct));
            row = result.ToDto();
            writeLsn = result.write_lsn;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "ux_inventory_client_service")
        {
            return Problem.Conflict(ErrorSlugs.DuplicateServiceNumber,
                "Duplicate service number",
                $"A row with service number '{req.ServiceNumber}' already exists for this tenant.");
        }

        lsn.RecordWrite(writeLsn);
        return Results.Created($"/api/v1/inventory/{row.id}", WireOf(row));
    }

    // ---- PATCH /inventory/{id}/status ---------------------------------
    public async Task<IResult> UpdateStatusAsync(Guid clientId, Guid id, StatusPatch patch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(patch.Status) || !InventoryStatuses.All.Contains(patch.Status))
            return Problem.ValidationFailed(new Dictionary<string, string[]>
                { ["status"] = [$"Must be one of {string.Join(", ", InventoryStatuses.All)}."] });

        await using var conn = await db.OpenPrimaryAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the target row; SELECT … FOR UPDATE serialises concurrent
        // patches on the same row without holding a table-level lock.
        var current = await conn.QuerySingleOrDefaultAsync<(string status, int rowVersion)?>(
            new CommandDefinition(
                "SELECT status, row_version FROM public.inventory WHERE id = @id AND client_id = @clientId FOR UPDATE",
                new { id, clientId }, transaction: tx, cancellationToken: ct));
        if (current is null) return Problem.NotFound();

        if (current.Value.rowVersion != patch.RowVersion)
            return Problem.Conflict(ErrorSlugs.ConcurrencyConflict,
                "Concurrency conflict",
                $"row_version mismatch (expected {current.Value.rowVersion}, got {patch.RowVersion}).");

        if (!StatusTransitions.IsAllowed(current.Value.status, patch.Status))
            return Problem.Conflict(ErrorSlugs.InvalidStatusTransition,
                "Invalid status transition",
                $"{current.Value.status} -> {patch.Status} is not a legal transition.");

        // Trigger bumps updated_at + row_version — see initial migration.
        var updated = await conn.QuerySingleAsync<UpdatedRow>(new CommandDefinition(
            @"UPDATE public.inventory
                SET status = @st
                WHERE id = @id AND client_id = @clientId
                RETURNING row_version, updated_at, pg_current_wal_lsn()::text AS write_lsn",
            new { id, clientId, st = patch.Status },
            transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        lsn.RecordWrite(updated.write_lsn);
        return Results.Ok(new StatusChangeResponse(
            Id: id,
            Status: patch.Status,
            RowVersion: updated.row_version,
            UpdatedAt: updated.updated_at));
    }

    // ---- wire helpers -------------------------------------------------

    private static InventoryRow WireOf(InventoryRowDto r) => ListHandler.WireOf(r);

    private sealed record CreateRowWithLsn
    {
        public Guid id { get; init; }
        public string service_number { get; init; } = "";
        public string product_category { get; init; } = "";
        public string product_name { get; init; } = "";
        public string status { get; init; } = "";
        public string? city { get; init; }
        public string? state { get; init; }
        public string? address { get; init; }
        public string? assignee { get; init; }
        public string? notes { get; init; }
        public DateTimeOffset created_at { get; init; }
        public DateTimeOffset updated_at { get; init; }
        public int row_version { get; init; }
        public string write_lsn { get; init; } = "";

        public InventoryRowDto ToDto() => new()
        {
            id = id, service_number = service_number,
            product_category = product_category, product_name = product_name,
            status = status, city = city, state = state, address = address,
            assignee = assignee, notes = notes,
            created_at = created_at, updated_at = updated_at, row_version = row_version,
        };
    }

    private sealed class UpdatedRow
    {
        public int row_version { get; set; }
        public DateTimeOffset updated_at { get; set; }
        public string write_lsn { get; set; } = "";
    }
}

public sealed record CreateRequest
{
    public string? ServiceNumber { get; init; }
    public string? ProductCategory { get; init; }
    public string? ProductName { get; init; }
    public string? Status { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? Address { get; init; }
    public string? Assignee { get; init; }
    public string? Notes { get; init; }
}

public sealed record StatusPatch(string Status, int RowVersion);
