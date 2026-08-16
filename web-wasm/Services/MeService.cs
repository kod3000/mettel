using Bruin.Web.Wasm.Models;

namespace Bruin.Web.Wasm.Services;

// Singleton that shadows the server's /me response for the current
// (tenant, role) pair. Re-fetches automatically when TenantContext raises
// OnChanged. Components subscribe to OnChanged to redraw when the role
// (or the admin-only-fields list) changes.
//
// Defensive default: before /me lands (and after any failure) the role
// falls back to Reader so write UI stays hidden. Server enforcement is
// authoritative regardless.
public sealed class MeService : IDisposable
{
    private readonly BruinApiClient _api;
    private readonly TenantContext _tenant;
    private MeResponse? _me;
    private bool _loading;

    public MeService(BruinApiClient api, TenantContext tenant)
    {
        _api = api;
        _tenant = tenant;
        _tenant.OnChanged += OnTenantChanged;
    }

    public MeResponse? Current => _me;
    public bool Loading => _loading;

    // Effective role — the server-echoed value if we've heard from /me,
    // otherwise the tenant-context's client-side selection (during initial
    // load) or Reader (after an error).
    public Role EffectiveRole => _me is not null
        ? RoleExtensions.FromWire(_me.Role)
        : _tenant.CurrentRole;

    public bool CanWrite => EffectiveRole is Role.Admin or Role.Worker;
    public bool CanDelete => EffectiveRole is Role.Admin;

    public event Action? OnChanged;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Clear cached response BEFORE fetching so EffectiveRole falls back
        // to the tenant-context's client-side role during the refresh
        // window. Otherwise a role flip (admin → reader) would show write
        // UI until the /me response landed.
        _me = null;
        _loading = true;
        OnChanged?.Invoke();
        try
        {
            _me = await _api.GetMeAsync(ct);
        }
        catch
        {
            _me = null; // fall back to Reader via EffectiveRole
        }
        finally
        {
            _loading = false;
            OnChanged?.Invoke();
        }
    }

    private void OnTenantChanged() => _ = RefreshAsync();

    public void Dispose() => _tenant.OnChanged -= OnTenantChanged;
}
