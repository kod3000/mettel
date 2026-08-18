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
    private string? _customKey;

    public Tenant Current => _tenant;
    public Role CurrentRole => _role;

    // A user-supplied X-Api-Key that overrides the built-in Client + Role
    // picker. Populated by pasting a key provisioned at
    // auth.mettel.exercise.dany.codes; persisted in localStorage by the
    // ClientPicker component. When non-null this wins on the wire.
    public string? CustomKey => _customKey;

    // Derived: the API key that goes on the wire for this request. Custom
    // key takes precedence — the server's IdentityFallback resolver knows
    // how to accept anything /resolve vouches for, so this is enough to
    // sign into any real tenant without editing localStorage by hand.
    public string CurrentApiKey => _customKey ?? _tenant.ApiKeyFor(_role);

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

    public void SetCustomKey(string? next)
    {
        var normalized = string.IsNullOrWhiteSpace(next) ? null : next!.Trim();
        if (normalized == _customKey) return;
        _customKey = normalized;
        OnChanged?.Invoke();
    }
}
