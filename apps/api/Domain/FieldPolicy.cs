namespace Bruin.Api.Domain;

// Per-tenant, per-field write allowlist. Admins can update any field;
// workers are gated by this table. Missing row = worker CAN write
// (permissive default — the API's per-field validation catches shape
// errors anyway). Explicit rows are only needed when a field must be
// admin-only (e.g. serviceNumber, productCategory).
//
// Reader never writes anything so isn't encoded here.
public sealed class FieldPolicy
{
    public Guid ClientId { get; set; }
    public string FieldName { get; set; } = "";
    // Minimum role required to write this field. 'admin' locks it to
    // admins only; 'worker' allows workers too (the default when a row
    // exists — omit the row entirely for the same effect).
    public string MinRole { get; set; } = Roles.Worker;
}
