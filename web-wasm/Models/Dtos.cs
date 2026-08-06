using System.Text.Json.Serialization;

namespace Bruin.Web.Wasm.Models;

// DTOs mirroring apps/api/openapi.v1.json component schemas. Hand-written
// (not generated) because the surface is small and drift is easy to spot
// in review. Property names match the OpenAPI schema exactly; the API
// serializes camelCase, so we tag each record with the JSON name.

public sealed record InventoryRow(
    [property: JsonPropertyName("id")]              string Id,
    [property: JsonPropertyName("serviceNumber")]   string ServiceNumber,
    [property: JsonPropertyName("productCategory")] string ProductCategory,
    [property: JsonPropertyName("productName")]     string ProductName,
    [property: JsonPropertyName("status")]          string Status,
    [property: JsonPropertyName("city")]            string? City,
    [property: JsonPropertyName("state")]           string? State,
    [property: JsonPropertyName("address")]         string? Address,
    [property: JsonPropertyName("assignee")]        string? Assignee,
    [property: JsonPropertyName("notes")]           string? Notes,
    [property: JsonPropertyName("createdAt")]       DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")]       DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("rowVersion")]      int RowVersion);

// Count is a "value + kind" envelope so the API can distinguish an exact
// count from a Postgres reltuples estimate — the grid label reflects
// which one the server returned.
public sealed record CountEnvelope(
    [property: JsonPropertyName("value")] long Value,
    [property: JsonPropertyName("kind")]  string Kind);

public sealed record ListResponse(
    [property: JsonPropertyName("rows")]          IReadOnlyList<InventoryRow> Rows,
    [property: JsonPropertyName("nextCursor")]    string? NextCursor,
    [property: JsonPropertyName("hasMore")]       bool HasMore,
    [property: JsonPropertyName("totalEstimate")] CountEnvelope? TotalEstimate,
    [property: JsonPropertyName("filteredCount")] CountEnvelope? FilteredCount,
    [property: JsonPropertyName("tookMs")]        long TookMs);

public sealed record StatusPatch(
    [property: JsonPropertyName("status")]     string Status,
    [property: JsonPropertyName("rowVersion")] int RowVersion);

public sealed record StatusChangeResponse(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("status")]     string Status,
    [property: JsonPropertyName("rowVersion")] int RowVersion,
    [property: JsonPropertyName("updatedAt")]  DateTimeOffset UpdatedAt);

public sealed record ProblemDetails(
    [property: JsonPropertyName("type")]     string? Type,
    [property: JsonPropertyName("title")]    string? Title,
    [property: JsonPropertyName("status")]   int?    Status,
    [property: JsonPropertyName("detail")]   string? Detail,
    [property: JsonPropertyName("instance")] string? Instance);
