namespace Bruin.Web.Wasm.Services;

// Per-tenant WAL LSN watermark. Writes stash `X-Write-LSN` from the
// mutation response here; reads echo it as `X-Min-LSN` so the API's
// read-router will fall back to the primary if the replica hasn't
// replayed the write yet (read-your-own-writes).
public sealed class LsnStore
{
    private readonly Dictionary<string, string> _map = new();
    private readonly object _lock = new();

    public string? Get(string tenantId)
    {
        lock (_lock) return _map.TryGetValue(tenantId, out var v) ? v : null;
    }

    public void Set(string tenantId, string lsn)
    {
        lock (_lock) _map[tenantId] = lsn;
    }
}
