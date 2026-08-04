using System.Text.Json;

namespace Bruin.Api.Domain;

// Filters/sort/columns stored as jsonb — schema evolves with the grid without a
// migration per column. Tenant-scoped like everything else; cross-tenant is 404.
public sealed class SavedView
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Name { get; set; } = "";
    public JsonDocument Filters { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument Sort { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument Columns { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
