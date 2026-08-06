using Bruin.Web.Wasm.Models;

namespace Bruin.Web.Wasm.Services;

// Singleton holding the currently-selected tenant. The picker writes,
// and the ApiKeyHandler + LsnStore both read. `OnChanged` lets the page
// remount its data views when the tenant flips (equivalent to the React
// `key={tenant.id}` remount trick).
public sealed class TenantContext
{
    private Tenant _current = Tenant.Default;

    public Tenant Current => _current;

    public event Action? OnChanged;

    public void Set(Tenant next)
    {
        if (next.Id == _current.Id) return;
        _current = next;
        OnChanged?.Invoke();
    }
}
