namespace Bruin.Api.Domain;

// Fixed vocabularies enforced as CHECK constraints (see migration), not PG enum
// types — altering a PG enum in a migration is painful and the reviewer will
// ask about adding a status. Kept in one place for the domain layer to import.
public static class ProductCategories
{
    public const string Voice = "voice";
    public const string Data = "data";
    public const string Wireless = "wireless";
    public const string Other = "other";
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Voice, Data, Wireless, Other };
}

public static class InventoryStatuses
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Disconnected = "disconnected";
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Pending, Active, Disconnected };
}

public sealed class Inventory
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }

    public string ServiceNumber { get; set; } = "";
    public string ProductCategory { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Status { get; set; } = "";

    public string? City { get; set; }
    public string? State { get; set; }
    public string? Address { get; set; }
    public string? Assignee { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Bumped by the DB on every UPDATE via trigger; drives optimistic concurrency.
    // Contract exposes it as `rowVersion` on the wire.
    public int RowVersion { get; set; }
}
