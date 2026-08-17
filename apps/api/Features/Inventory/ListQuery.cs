namespace Bruin.Api.Features.Inventory;

// Parsed + validated shape of GET /api/v1/inventory. Handler code touches this,
// not raw HttpRequest — makes the SQL path unit-testable without a live server.
public sealed record ListQuery
{
    public string? Q { get; init; }
    // Optional narrowing of the search predicate to a subset of columns.
    // Wire values: productName | productCategory | serviceNumber | status |
    // city | state | address | assignee | notes. Empty (default) = search
    // all indexed columns via the tsvector + service_number trigram (fast).
    // When non-empty, the handler swaps to a per-column ILIKE OR — slower
    // for broad matches but gives the operator surgical control.
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Statuses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> States { get; init; } = Array.Empty<string>();
    public SortKey Sort { get; init; } = SortKey.CreatedAt;
    public SortDirection Dir { get; init; } = SortDirection.Desc;
    public int PageSize { get; init; } = 100;
    public string? Cursor { get; init; }

    public bool HasFilters =>
        Statuses.Count > 0 ||
        Categories.Count > 0 ||
        States.Count > 0 ||
        (Q is not null && Q.Length >= 2);
}

public enum SortKey { CreatedAt, UpdatedAt, Status, ServiceNumber, ProductName }
public enum SortDirection { Asc, Desc }

public static class SortKeyExtensions
{
    // Column name pushed into SQL. Fixed list, never user input — safe against
    // injection because the enum is the interface, not a string.
    public static string ToColumn(this SortKey s) => s switch
    {
        SortKey.CreatedAt     => "created_at",
        SortKey.UpdatedAt     => "updated_at",
        SortKey.Status        => "status",
        SortKey.ServiceNumber => "service_number",
        SortKey.ProductName   => "product_name",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null)
    };

    public static string ToWire(this SortKey s) => s switch
    {
        SortKey.CreatedAt     => "createdAt",
        SortKey.UpdatedAt     => "updatedAt",
        SortKey.Status        => "status",
        SortKey.ServiceNumber => "serviceNumber",
        SortKey.ProductName   => "productName",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null)
    };

    public static bool TryParseSort(string? s, out SortKey key)
    {
        switch (s)
        {
            case null: case "": case "createdAt": key = SortKey.CreatedAt; return true;
            case "updatedAt":     key = SortKey.UpdatedAt;     return true;
            case "status":        key = SortKey.Status;        return true;
            case "serviceNumber": key = SortKey.ServiceNumber; return true;
            case "productName":   key = SortKey.ProductName;   return true;
            default: key = default; return false;
        }
    }

    public static bool TryParseDir(string? s, out SortDirection d)
    {
        switch (s)
        {
            case null: case "": case "desc": d = SortDirection.Desc; return true;
            case "asc": d = SortDirection.Asc; return true;
            default: d = default; return false;
        }
    }
}
