namespace Bruin.Web.Wasm.Models;

// Public tenant list mirrors the React SPA. Keys are intentionally
// non-consequential — the demo has no real auth.
public sealed record Tenant(string Id, string Label, string ApiKey)
{
    public static readonly IReadOnlyList<Tenant> All = new[]
    {
        new Tenant("acme",    "Acme Telecom",           "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme"),
        new Tenant("beacon",  "Beacon Networks",        "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_beacon"),
        new Tenant("cascade", "Cascade Communications", "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_cascade"),
    };

    public static Tenant Default => All[0];
}
