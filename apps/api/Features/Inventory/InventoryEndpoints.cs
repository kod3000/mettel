using Bruin.Api.Domain;
using Bruin.Api.Errors;
using Bruin.Api.Features.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bruin.Api.Features.Inventory;

public static class InventoryEndpoints
{
    public static void MapInventory(this IEndpointRouteBuilder r)
    {
        r.MapGet("/api/v1/inventory", ListInventoryAsync)
            .Produces<Contracts.ListResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401);
        r.MapGet("/api/v1/inventory/snapshot", SnapshotInventoryAsync)
            .Produces<Contracts.SnapshotResponse>()
            .ProducesProblem(400)
            .ProducesProblem(401);
        r.MapGet("/api/v1/inventory/{id:guid}", GetInventoryAsync)
            .Produces<Contracts.InventoryRow>()
            .ProducesProblem(404);
        r.MapPost("/api/v1/inventory", CreateInventoryAsync)
            .RequireRole(Roles.Admin, Roles.Worker)
            .Produces<Contracts.InventoryRow>(StatusCodes.Status201Created)
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(409);
        r.MapPatch("/api/v1/inventory/{id:guid}/status", PatchStatusAsync)
            .RequireRole(Roles.Admin, Roles.Worker)
            .Produces<Contracts.StatusChangeResponse>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409);
        r.MapPatch("/api/v1/inventory/{id:guid}", PatchInventoryAsync)
            .RequireRole(Roles.Admin, Roles.Worker)
            .Produces<Contracts.InventoryRow>()
            .ProducesProblem(400)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(409);
        r.MapDelete("/api/v1/inventory/{id:guid}", DeleteInventoryAsync)
            .RequireRole(Roles.Admin)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(403)
            .ProducesProblem(404);
    }

    private static Task<IResult> GetInventoryAsync(
        Guid id, ITenantContext tenant, WriteHandler h, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Task.FromResult(Problem.Unauthorized());
        return h.GetAsync(clientId, id, ct);
    }

    // Snapshot endpoint feeds the WASM client's local SQLite mirror. `since`
    // + `sinceId` come from the client's last-persisted watermark; both null
    // means "start from the beginning". Includes tombstones (deleted rows)
    // so the mirror can tombstone in step with the primary.
    private static async Task<IResult> SnapshotInventoryAsync(
        ITenantContext tenant,
        SnapshotHandler handler,
        CancellationToken ct,
        DateTimeOffset? since,
        Guid? sinceId,
        int? limit)
    {
        if (tenant.ClientId is not Guid clientId) return Problem.Unauthorized();

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        // Both watermarks must be supplied together — a bare `since` without
        // `sinceId` would page-skip on same-timestamp rows.
        if ((since is null) != (sinceId is null))
            errors["since"] = new[] { "since and sinceId must be supplied together." };
        if (limit is int l && (l < 1 || l > SnapshotHandler.MaxLimit))
            errors["limit"] = new[] { $"Must be between 1 and {SnapshotHandler.MaxLimit}." };
        if (errors.Count > 0) return Problem.ValidationFailed(errors);

        var res = await handler.Handle(clientId, since, sinceId, limit, ct);
        return Results.Ok(res);
    }

    private static Task<IResult> CreateInventoryAsync(
        CreateRequest body, ITenantContext tenant, WriteHandler h, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Task.FromResult(Problem.Unauthorized());
        return h.CreateAsync(clientId, body, ct);
    }

    private static Task<IResult> PatchStatusAsync(
        Guid id, StatusPatch body, ITenantContext tenant, WriteHandler h, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Task.FromResult(Problem.Unauthorized());
        return h.UpdateStatusAsync(clientId, id, body, ct);
    }

    private static async Task<IResult> PatchInventoryAsync(
        Guid id, HttpContext ctx, ITenantContext tenant, WriteHandler h, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Problem.Unauthorized();
        if (tenant.Role is not string role)       return Problem.Unauthorized();
        // Read the body as JsonElement so the handler can distinguish
        // absent fields from explicit-null (clear).
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
        return await h.UpdateAsync(clientId, id, doc.RootElement, role, ct);
    }

    private static Task<IResult> DeleteInventoryAsync(
        Guid id, ITenantContext tenant, WriteHandler h, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Task.FromResult(Problem.Unauthorized());
        return h.DeleteAsync(clientId, id, ct);
    }

    // Endpoint stays thin — parsing + validation → handler → response.
    // Additional endpoints (GET-by-id, POST, PATCH) land in Phase 6.
    private static async Task<IResult> ListInventoryAsync(
        HttpContext ctx,
        ITenantContext tenant,
        ListHandler handler,
        CancellationToken ct,
        string? q,
        string? sort,
        string? dir,
        int? pageSize,
        string? cursor)
    {
        if (tenant.ClientId is not Guid clientId)
            return Problem.Unauthorized();

        var query = ctx.Request.Query;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!SortKeyExtensions.TryParseSort(sort, out var sortKey))
            errors["sort"] = new[] { "Must be one of createdAt, updatedAt, status, serviceNumber, productName." };
        if (!SortKeyExtensions.TryParseDir(dir, out var dirVal))
            errors["dir"] = new[] { "Must be asc or desc." };

        // Repeated params bind through IQueryCollection so the framework's
        // single-value ?status=x binding doesn't lose entries when the caller
        // sends ?status=a&status=b.
        var statuses   = query["status"].Where(NonEmpty).Select(s => s!).ToArray();
        var categories = query["productCategory"].Where(NonEmpty).Select(s => s!).ToArray();
        var states     = query["state"].Where(NonEmpty).Select(s => s!).ToArray();
        // fields= accepts both repeated-key (?fields=a&fields=b) and
        // comma-separated (?fields=a,b) shapes for operator convenience.
        // Unknown entries are silently dropped by the handler's whitelist.
        var fields = query["fields"]
            .Where(NonEmpty)
            .SelectMany(s => s!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(NonEmpty)
            .ToArray();

        foreach (var s in statuses)
            if (!InventoryStatuses.All.Contains(s))
                errors["status"] = new[] { $"Unknown status '{s}'." };
        foreach (var c in categories)
            if (!ProductCategories.All.Contains(c))
                errors["productCategory"] = new[] { $"Unknown category '{c}'." };

        if (errors.Count > 0) return Problem.ValidationFailed(errors);

        var listQuery = new ListQuery
        {
            Q = q,
            Fields = fields,
            Statuses = statuses,
            Categories = categories,
            States = states,
            Sort = sortKey,
            Dir = dirVal,
            PageSize = pageSize ?? 100,
            Cursor = cursor,
        };

        var result = await handler.Handle(clientId, listQuery, ct);
        // Handler returns either IResult (error) or Results.Json(...) (success).
        return result as IResult ?? Results.Ok(result);
    }

    private static bool NonEmpty(string? s) => !string.IsNullOrWhiteSpace(s);
}
