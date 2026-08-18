namespace Bruin.Api.Contracts;

// Wire-facing DTOs. Kept in one file so the schema surface is easy to eyeball
// against docs/API_CONTRACT.md. Only these types show up in the generated
// OpenAPI + TypeScript client — handlers must not return anonymous objects
// anymore (they don't show up in the schema, which breaks Phase 7 codegen).

// GET /api/v1/debug/lsn — powers the bottom-of-screen LSN status bar in the SPA.
public sealed record DebugLsnResponse(
    string? Primary,
    string? Replica,
    long LagBytes,
    double LagSeconds,
    bool Reachable);

// GET /api/v1/me — SPA fetches once on mount to gate role-restricted UI.
// `ClientName` mirrors `client.name` so the SPA can render which tenant a
// pasted custom key resolved to; empty string when the client row has no
// name set (older seed rows).
public sealed record MeResponse(
    Guid ClientId,
    string ClientName,
    string Role,
    IReadOnlyList<string> AdminOnlyFields);

public sealed record InventoryRow(
    Guid Id,
    string ServiceNumber,
    string ProductCategory,
    string ProductName,
    string Status,
    string? City,
    string? State,
    string? Address,
    string? Assignee,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int RowVersion);

// `kind` is one of `exact | approximate | atLeast`. Kept as a string to
// match the contract's freeform-slug shape; a stricter typed enum in the
// generated TS client can narrow.
public sealed record CountEnvelope(long Value, string Kind);

public sealed record ListResponse(
    IReadOnlyList<InventoryRow> Rows,
    string? NextCursor,
    bool HasMore,
    CountEnvelope? TotalEstimate,
    CountEnvelope? FilteredCount,
    long TookMs);

// Snapshot rows for the local-replica hydration/delta protocol.
// Adds `deletedAt` so the client can tombstone rows from its local mirror.
public sealed record SnapshotRow(
    Guid Id,
    string ServiceNumber,
    string ProductCategory,
    string ProductName,
    string Status,
    string? City,
    string? State,
    string? Address,
    string? Assignee,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int RowVersion,
    DateTimeOffset? DeletedAt);

// The client persists (NextSince, NextSinceId) after each successful page
// and re-issues them as `since` / `sinceId` on the next call. When
// HasMore=false the caller has reached the head of the tenant's inventory.
public sealed record SnapshotResponse(
    IReadOnlyList<SnapshotRow> Rows,
    DateTimeOffset? NextSince,
    Guid? NextSinceId,
    bool HasMore,
    long TookMs);

public sealed record StatusChangeResponse(
    Guid Id,
    string Status,
    int RowVersion,
    DateTimeOffset UpdatedAt);

// Saved-view API. Filters/Sort/Columns are opaque JSON payloads whose shape
// the UI owns — the API just persists them.
public sealed record SavedViewUpsert(
    string Name,
    string? Filters,
    string? Sort,
    string? Columns);

public sealed record SavedViewResponse(
    Guid Id,
    string Name,
    System.Text.Json.JsonElement Filters,
    System.Text.Json.JsonElement Sort,
    System.Text.Json.JsonElement Columns,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SavedViewList(IReadOnlyList<SavedViewResponse> Views);

// Bulk-job API.
public sealed record BulkJobAccepted(Guid JobId, string Status);

public sealed record BulkJobStatus(
    Guid JobId,
    string Status,
    string FileName,
    int TotalRows,
    int ProcessedRows,
    int SucceededRows,
    int FailedRows,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string ErrorSampleUrl);

public sealed record BulkJobError(
    int RowNumber,
    string? ServiceNumber,
    string Reason,
    string RawLine);

public sealed record BulkJobErrors(IReadOnlyList<BulkJobError> Errors);
