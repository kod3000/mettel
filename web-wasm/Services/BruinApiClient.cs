using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Bruin.Web.Wasm.Models;

namespace Bruin.Web.Wasm.Services;

// Typed wrapper around HttpClient for the endpoints this SPA calls.
// Matches the React `apps/web/src/api/inventory.ts` surface so the two
// clients are directly comparable in the perf benchmark.
//
// Every method that hits an inventory endpoint funnels through
// `ThrowIfProblem` — 4xx/5xx responses are parsed as ProblemDetails and
// re-thrown as ApiException so the UI can switch on `.Slug`.
public sealed class BruinApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public BruinApiClient(HttpClient http) => _http = http;

    // ---- Inventory list (keyset-paginated) ------------------------------

    public sealed record ListParams(
        string? Q = null,
        IReadOnlyCollection<string>? Status = null,
        IReadOnlyCollection<string>? ProductCategory = null,
        IReadOnlyCollection<string>? State = null,
        string? Sort = null,
        string? Dir = null,
        int? PageSize = null,
        string? Cursor = null);

    public async Task<ListResponse> ListAsync(ListParams p, CancellationToken ct = default)
    {
        var qs = HttpUtility.ParseQueryString(string.Empty);
        if (!string.IsNullOrWhiteSpace(p.Q))    qs["q"] = p.Q;
        if (!string.IsNullOrWhiteSpace(p.Sort)) qs["sort"] = p.Sort;
        if (!string.IsNullOrWhiteSpace(p.Dir))  qs["dir"] = p.Dir;
        if (p.PageSize.HasValue)                qs["pageSize"] = p.PageSize.Value.ToString();
        if (!string.IsNullOrWhiteSpace(p.Cursor)) qs["cursor"] = p.Cursor;
        AppendMulti(qs, "status", p.Status);
        AppendMulti(qs, "productCategory", p.ProductCategory);
        AppendMulti(qs, "state", p.State);

        var url = $"api/v1/inventory?{qs}";
        using var res = await _http.GetAsync(url, ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<ListResponse>(Json, ct))!;
    }

    public async Task<InventoryRow> GetAsync(string id, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"api/v1/inventory/{Uri.EscapeDataString(id)}", ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<InventoryRow>(Json, ct))!;
    }

    public async Task<StatusChangeResponse> PatchStatusAsync(
        string id, StatusPatch body, CancellationToken ct = default)
    {
        using var res = await _http.PatchAsJsonAsync(
            $"api/v1/inventory/{Uri.EscapeDataString(id)}/status", body, Json, ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<StatusChangeResponse>(Json, ct))!;
    }

    // ---- helpers --------------------------------------------------------

    private static void AppendMulti(
        System.Collections.Specialized.NameValueCollection qs,
        string key, IReadOnlyCollection<string>? values)
    {
        if (values is null) return;
        foreach (var v in values) qs.Add(key, v);
    }

    private static async Task ThrowIfProblem(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        ProblemDetails? p = null;
        try { p = await res.Content.ReadFromJsonAsync<ProblemDetails>(Json, ct); }
        catch { /* server may not have returned JSON */ }
        p ??= new ProblemDetails(null, res.ReasonPhrase, (int)res.StatusCode, null, null);
        throw new ApiException(p);
    }
}
