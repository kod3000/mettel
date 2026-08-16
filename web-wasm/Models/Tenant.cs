namespace Bruin.Web.Wasm.Models;

// Public tenant list mirrors the React SPA. Keys are intentionally
// non-consequential — the demo has no real auth. Each tenant carries the
// admin key; the worker/reader keys are derived by the migration-mandated
// suffix (see `ApiKeyFor`) so the same convention lives in one place.
public sealed record Tenant(string Id, string Label, string ApiKey)
{
    public static readonly IReadOnlyList<Tenant> All = new[]
    {
        new Tenant("acme",    "Acme Telecom",           "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme"),
        new Tenant("beacon",  "Beacon Networks",        "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_beacon"),
        new Tenant("cascade", "Cascade Communications", "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_cascade"),
    };

    public static Tenant Default => All[0];

    // Matches the A1 migration seed: admin → <base>, worker → <base>_worker,
    // reader → <base>_reader.
    public string ApiKeyFor(Role role) => role switch
    {
        Role.Admin  => ApiKey,
        Role.Worker => $"{ApiKey}_worker",
        Role.Reader => $"{ApiKey}_reader",
        _ => ApiKey,
    };
}

public enum Role { Admin, Worker, Reader }

public static class RoleExtensions
{
    public static string ToWire(this Role role) => role switch
    {
        Role.Admin  => "admin",
        Role.Worker => "worker",
        Role.Reader => "reader",
        _ => "reader",
    };

    public static Role FromWire(string? role) => role switch
    {
        "admin"  => Role.Admin,
        "worker" => Role.Worker,
        _        => Role.Reader,
    };
}
