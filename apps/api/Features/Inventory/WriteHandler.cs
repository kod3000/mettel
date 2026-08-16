using System.Text.Json;
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
            WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL";

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
                "SELECT status, row_version FROM public.inventory WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL FOR UPDATE",
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

    // ---- PATCH /inventory/{id} ---------------------------------------
    //
    // Body: JSON object with an int `rowVersion` (required for optimistic
    // concurrency) plus any subset of writable fields. Fields absent from
    // the JSON are left untouched; fields present with null CLEAR the
    // column (nullable columns only). Status is NOT accepted here — it
    // routes through PATCH /{id}/status which enforces the transition
    // FSM. Attempts to include it in this body get a per-field error.
    //
    // Field-level authorization: fields listed in field_policy with
    // min_role='admin' are admin-only. A worker request that touches
    // any of them 403s with a per-field error map so the SPA can
    // highlight the offending inputs.
    public async Task<IResult> UpdateAsync(
        Guid clientId, Guid id, JsonElement body, string role, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return Problem.ValidationFailed(new Dictionary<string, string[]>
                { ["_"] = ["Body must be a JSON object."] });

        if (!body.TryGetProperty("rowVersion", out var rvEl) || rvEl.ValueKind != JsonValueKind.Number)
            return Problem.ValidationFailed(new Dictionary<string, string[]>
                { ["rowVersion"] = ["Required (int) for optimistic concurrency."] });
        var patchRowVersion = rvEl.GetInt32();

        // Whitelist writable columns (wire-name → sql-column). Status is
        // deliberately excluded (see /status endpoint). serviceNumber is
        // included but the unique constraint will 409 on collision.
        var writable = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["serviceNumber"]    = "service_number",
            ["productCategory"]  = "product_category",
            ["productName"]      = "product_name",
            ["city"]             = "city",
            ["state"]            = "state",
            ["address"]          = "address",
            ["assignee"]         = "assignee",
            ["notes"]            = "notes",
        };

        var fieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var updates = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var prop in body.EnumerateObject())
        {
            if (prop.NameEquals("rowVersion")) continue;
            if (prop.NameEquals("status"))
            {
                fieldErrors["status"] = ["Use PATCH /inventory/{id}/status to change status — the FSM lives there."];
                continue;
            }
            if (!writable.TryGetValue(prop.Name, out var column))
            {
                fieldErrors[prop.Name] = ["Unknown or non-writable field."];
                continue;
            }

            // Per-field validation of the shape of the value.
            var value = ReadValue(prop.Value, prop.Name, fieldErrors);
            if (value is FailedRead) continue;

            if (prop.NameEquals("productCategory") && value is string cat
                && !ProductCategories.All.Contains(cat))
            {
                fieldErrors["productCategory"] = [$"Must be one of {string.Join(", ", ProductCategories.All)}."];
                continue;
            }
            if (prop.NameEquals("serviceNumber") && value is string sn && string.IsNullOrWhiteSpace(sn))
            {
                fieldErrors["serviceNumber"] = ["Cannot be empty."];
                continue;
            }
            if (prop.NameEquals("productName") && value is string pn && string.IsNullOrWhiteSpace(pn))
            {
                fieldErrors["productName"] = ["Cannot be empty."];
                continue;
            }

            updates[column] = value is NullValue ? null : value;
        }

        if (updates.Count == 0 && fieldErrors.Count == 0)
            return Problem.ValidationFailed(new Dictionary<string, string[]>
                { ["_"] = ["No writable fields present in patch."] });

        // Field-policy check — admin-only fields locked out for workers.
        // Admins bypass entirely. Reader is already 403'd at the endpoint.
        if (role == Roles.Worker && updates.Count > 0)
        {
            await using var policyConn = await db.OpenReplicaAsync(ct);
            var adminOnly = (await policyConn.QueryAsync<string>(new CommandDefinition(@"
                SELECT field_name FROM public.field_policy
                WHERE client_id = @cid AND min_role = 'admin'",
                new { cid = clientId }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
            // Map back from sql-column to wire-name so the error keys match
            // what the SPA sent (better DX than surfacing snake_case).
            var wireByCol = writable.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
            foreach (var col in updates.Keys.ToArray())
            {
                var wire = wireByCol[col];
                if (adminOnly.Contains(wire))
                {
                    fieldErrors[wire] = ["Admin-only field — worker role cannot modify."];
                    updates.Remove(col);
                }
            }
        }

        if (fieldErrors.Count > 0) return Problem.ValidationFailed(fieldErrors);

        await using var conn = await db.OpenPrimaryAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var current = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT row_version FROM public.inventory WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL FOR UPDATE",
            new { id, clientId }, transaction: tx, cancellationToken: ct));
        if (current is null) return Problem.NotFound();
        if (current.Value != patchRowVersion)
            return Problem.Conflict(ErrorSlugs.ConcurrencyConflict,
                "Concurrency conflict",
                $"row_version mismatch (expected {current.Value}, got {patchRowVersion}).");

        // Build parameterized UPDATE from the updates dictionary.
        var setClauses = updates.Keys.Select(c => $"{c} = @{c}").ToArray();
        var sql = $@"
            UPDATE public.inventory
            SET {string.Join(", ", setClauses)}
            WHERE id = @id AND client_id = @clientId
            RETURNING id, service_number, product_category, product_name, status,
                      city, state, address, assignee, notes,
                      created_at, updated_at, row_version,
                      pg_current_wal_lsn()::text AS write_lsn";

        var parameters = new DynamicParameters();
        parameters.Add("id", id);
        parameters.Add("clientId", clientId);
        foreach (var (col, val) in updates) parameters.Add(col, val);

        CreateRowWithLsn result;
        try
        {
            result = await conn.QuerySingleAsync<CreateRowWithLsn>(
                new CommandDefinition(sql, parameters, transaction: tx, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == "23505" && ex.ConstraintName == "ux_inventory_client_service")
        {
            return Problem.Conflict(ErrorSlugs.DuplicateServiceNumber,
                "Duplicate service number",
                "A row with that service number already exists for this tenant.");
        }

        await tx.CommitAsync(ct);
        lsn.RecordWrite(result.write_lsn);
        return Results.Ok(WireOf(result.ToDto()));
    }

    // Marker types so ReadValue can distinguish "the caller sent JSON null
    // (clear the column)" from "the caller sent a bad shape (skip and
    // record an error)". Never returned to the wire.
    private sealed record FailedRead;
    private sealed record NullValue;
    private static readonly FailedRead _failedRead = new();
    private static readonly NullValue _nullValue = new();

    private static object? ReadValue(JsonElement el, string field, Dictionary<string, string[]> errors)
    {
        // All writable inventory fields are strings on the wire. Numeric /
        // boolean payloads for a string column are almost always a client
        // bug — reject with a per-field error instead of silently coercing.
        switch (el.ValueKind)
        {
            case JsonValueKind.Null: return _nullValue;
            case JsonValueKind.String: return el.GetString();
            default:
                errors[field] = ["Must be a string or null."];
                return _failedRead;
        }
    }

    // ---- DELETE /inventory/{id} — soft delete ------------------------
    //
    // Sets deleted_at to now(); every read path filters `WHERE deleted_at
    // IS NULL` so the row disappears from list/get/patch immediately. The
    // partial unique index on (client_id, service_number) WHERE deleted_at
    // IS NULL frees up the service number so a fresh row can be inserted
    // under the same identifier. Admin-only at the endpoint. Idempotent:
    // deleting an already-deleted row returns 404 (same as a non-existent
    // row).
    public async Task<IResult> DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenPrimaryAsync(ct);
        var writeLsn = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(@"
            UPDATE public.inventory
                SET deleted_at = now()
                WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL
                RETURNING pg_current_wal_lsn()::text",
            new { id, clientId }, cancellationToken: ct));

        if (writeLsn is null) return Problem.NotFound();
        lsn.RecordWrite(writeLsn);
        return Results.NoContent();
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
