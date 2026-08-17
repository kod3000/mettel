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
    [property: JsonPropertyName("instance")] string? Instance,
    // ASP.NET Core validation errors ship a `errors` object keyed by
    // property name → array of messages. Not in the base RFC 7807 shape
    // but universal in .NET APIs; the CreateInventoryModal renders these
    // inline against the offending field.
    [property: JsonPropertyName("errors")]   Dictionary<string, string[]>? Errors = null);

public sealed record CreateRequest(
    [property: JsonPropertyName("serviceNumber")]   string? ServiceNumber,
    [property: JsonPropertyName("productCategory")] string? ProductCategory,
    [property: JsonPropertyName("productName")]     string? ProductName,
    [property: JsonPropertyName("status")]          string? Status,
    [property: JsonPropertyName("city")]            string? City = null,
    [property: JsonPropertyName("state")]           string? State = null,
    [property: JsonPropertyName("address")]         string? Address = null,
    [property: JsonPropertyName("assignee")]        string? Assignee = null,
    [property: JsonPropertyName("notes")]           string? Notes = null);

public sealed record SavedView(
    [property: JsonPropertyName("id")]        string? Id,
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("filters")]   string Filters,
    [property: JsonPropertyName("sort")]      string? Sort,
    [property: JsonPropertyName("columns")]   string? Columns,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt = null);

public sealed record SavedViewList(
    [property: JsonPropertyName("views")] IReadOnlyList<SavedView> Views);

public sealed record SavedViewUpsert(
    [property: JsonPropertyName("name")]    string Name,
    [property: JsonPropertyName("filters")] string Filters,
    [property: JsonPropertyName("sort")]    string? Sort = null,
    [property: JsonPropertyName("columns")] string? Columns = null);

// GET /api/v1/debug/lsn — powers the LSN status bar.
public sealed record DebugLsnResponse(
    [property: JsonPropertyName("primary")]    string? Primary,
    [property: JsonPropertyName("replica")]    string? Replica,
    [property: JsonPropertyName("lagBytes")]   long    LagBytes,
    [property: JsonPropertyName("lagSeconds")] double  LagSeconds,
    [property: JsonPropertyName("reachable")]  bool    Reachable);

// GET /api/v1/me — fetched once per (tenant, role) change to gate write UI.
public sealed record MeResponse(
    [property: JsonPropertyName("clientId")]       string ClientId,
    [property: JsonPropertyName("role")]           string Role,
    [property: JsonPropertyName("adminOnlyFields")] IReadOnlyList<string> AdminOnlyFields);

public sealed record BulkJobAccepted(
    [property: JsonPropertyName("jobId")]  string JobId,
    [property: JsonPropertyName("status")] string Status);

public sealed record BulkJobStatus(
    [property: JsonPropertyName("jobId")]          string JobId,
    [property: JsonPropertyName("status")]         string Status,
    [property: JsonPropertyName("fileName")]       string? FileName,
    [property: JsonPropertyName("totalRows")]      int TotalRows,
    [property: JsonPropertyName("processedRows")]  int ProcessedRows,
    [property: JsonPropertyName("succeededRows")]  int SucceededRows,
    [property: JsonPropertyName("failedRows")]     int FailedRows,
    [property: JsonPropertyName("startedAt")]      DateTimeOffset? StartedAt = null,
    [property: JsonPropertyName("completedAt")]    DateTimeOffset? CompletedAt = null,
    [property: JsonPropertyName("errorSampleUrl")] string? ErrorSampleUrl = null);

public sealed record BulkJobErrorEntry(
    [property: JsonPropertyName("rowNumber")]     int RowNumber,
    [property: JsonPropertyName("serviceNumber")] string? ServiceNumber,
    [property: JsonPropertyName("reason")]        string Reason,
    [property: JsonPropertyName("rawLine")]       string? RawLine);

public sealed record BulkJobErrorsResponse(
    [property: JsonPropertyName("errors")] IReadOnlyList<BulkJobErrorEntry> Errors);
