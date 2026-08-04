namespace Bruin.Api.Domain;

// Resolved from X-Api-Key on every request (Phase 6 middleware). Scoped service
// consumed by the DbContext's global query filter for defence in depth — the
// authoritative tenancy check is still the client_id predicate in every SQL
// statement. Cross-tenant reads must return 404, never 403.
public interface ITenantContext
{
    Guid? ClientId { get; }
    void Set(Guid clientId);
}

public sealed class TenantContext : ITenantContext
{
    public Guid? ClientId { get; private set; }

    public void Set(Guid clientId)
    {
        if (clientId == Guid.Empty)
            throw new InvalidOperationException("client id must not be empty");
        if (ClientId is not null && ClientId != clientId)
            throw new InvalidOperationException("tenant already bound on this scope");
        ClientId = clientId;
    }
}
