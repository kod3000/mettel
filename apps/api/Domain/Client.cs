namespace Bruin.Api.Domain;

// Tenant. api_key is looked up per request (Phase 6); it identifies which client
// is on the wire — the tenant is *never* accepted from the body, query, or cursor.
public sealed class Client
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
