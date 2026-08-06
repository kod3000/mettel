using Bruin.Web.Wasm.Services;

namespace Bruin.Web.Wasm.Handlers;

// DelegatingHandler that:
//   1. Injects X-Api-Key on every request (from TenantContext).
//   2. Echoes X-Min-LSN when the LsnStore has a watermark for this
//      tenant — the API's read-router uses it to decide primary vs
//      replica routing.
//   3. Captures X-Write-LSN off any response (even non-2xx, so a 409
//      that still bumped the row doesn't drop the watermark) and stashes
//      it back into the LsnStore.
//
// Mirrors the React client (`apps/web/src/api/client.ts`).
public sealed class ApiKeyHandler : DelegatingHandler
{
    private readonly TenantContext _tenant;
    private readonly LsnStore _lsn;

    public ApiKeyHandler(TenantContext tenant, LsnStore lsn)
    {
        _tenant = tenant;
        _lsn = lsn;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var tenant = _tenant.Current;
        request.Headers.Remove("X-Api-Key");
        request.Headers.Add("X-Api-Key", tenant.ApiKey);

        var min = _lsn.Get(tenant.Id);
        if (!string.IsNullOrEmpty(min))
        {
            request.Headers.Remove("X-Min-LSN");
            request.Headers.Add("X-Min-LSN", min);
        }

        var res = await base.SendAsync(request, ct);

        if (res.Headers.TryGetValues("X-Write-LSN", out var vals))
        {
            var write = vals.FirstOrDefault();
            if (!string.IsNullOrEmpty(write)) _lsn.Set(tenant.Id, write);
        }

        return res;
    }
}
