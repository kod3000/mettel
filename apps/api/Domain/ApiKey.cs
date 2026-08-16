namespace Bruin.Api.Domain;

// One row per issued API key. Splits off from client so a single tenant
// can have multiple keys with different roles (admin/worker/reader) —
// the original client.api_key column is preserved for backward
// compatibility with the seeder and rolls to legacy 'admin' entries.
//
// Role is checked in the auth middleware and enforced per endpoint via
// the [RequireRole(...)] filter helper. See Features/Tenancy.
public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string Key { get; set; } = "";
    public string Role { get; set; } = Roles.Reader;
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// Role names — string constants (not an enum) because they land in the
// database as varchar and are echoed verbatim in the /me response the
// SPA consumes. Keeping the wire form and the code form identical
// avoids a mapping layer for zero benefit.
public static class Roles
{
    public const string Admin  = "admin";
    public const string Worker = "worker";
    public const string Reader = "reader";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Admin, Worker, Reader };

    public static bool IsKnown(string? role) => role is not null && All.Contains(role);
}
