using Bruin.Web.Wasm.Models;

namespace Bruin.Web.Wasm.Services;

// Singleton holding the currently-selected (tenant, role) pair. The picker
// writes; the ApiKeyHandler + LsnStore + MeService read. `OnChanged` lets
// pages remount their data views whenever either flips (equivalent to the
// React `key={tenant.id}:{role}` remount trick).
public sealed class TenantContext
{
    private Tenant _tenant = Tenant.Default;
    private Role _role = Role.Admin;

    public Tenant Current => _tenant;
    public Role CurrentRole => _role;

    // Derived: the API key that goes on the wire for this (tenant, role).
    public string CurrentApiKey => _tenant.ApiKeyFor(_role);

    public event Action? OnChanged;

    public void Set(Tenant next)
    {
        if (next.Id == _tenant.Id) return;
        _tenant = next;
        OnChanged?.Invoke();
    }

    public void SetRole(Role next)
    {
        if (next == _role) return;
        _role = next;
        OnChanged?.Invoke();
    }
}
