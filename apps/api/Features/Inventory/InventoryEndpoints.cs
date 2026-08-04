using Bruin.Api.Domain;
using Bruin.Api.Errors;
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
        r.MapGet("/api/v1/inventory/{id:guid}", GetInventoryAsync)
            .Produces<Contracts.InventoryRow>()
            .ProducesProblem(404);
        r.MapPost("/api/v1/inventory", CreateInventoryAsync)
            .Produces<Contracts.InventoryRow>(StatusCodes.Status201Created)
            .ProducesProblem(400)
            .ProducesProblem(409);
        r.MapPatch("/api/v1/inventory/{id:guid}/status", PatchStatusAsync)
            .Produces<Contracts.StatusChangeResponse>()
            .ProducesProblem(400)
            .ProducesProblem(404)
            .ProducesProblem(409);
    }

    private static Task<IResult> GetInventoryAsync(
        Guid id, ITenantContext tenant, WriteHandler h, CancellationToken ct)
    {
        if (tenant.ClientId is not Guid clientId) return Task.FromResult(Problem.Unauthorized());
        return h.GetAsync(clientId, id, ct);
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
