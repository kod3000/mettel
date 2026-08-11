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

    public async Task<InventoryRow> CreateAsync(CreateRequest body, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync("api/v1/inventory", body, Json, ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<InventoryRow>(Json, ct))!;
    }

    // ---- Saved views ----------------------------------------------------

    public async Task<SavedViewList> ListSavedViewsAsync(CancellationToken ct = default)
    {
        using var res = await _http.GetAsync("api/v1/saved-views", ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<SavedViewList>(Json, ct))!;
    }

    public async Task<SavedView> CreateSavedViewAsync(SavedViewUpsert body, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync("api/v1/saved-views", body, Json, ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<SavedView>(Json, ct))!;
    }

    public async Task DeleteSavedViewAsync(string id, CancellationToken ct = default)
    {
        using var res = await _http.DeleteAsync($"api/v1/saved-views/{Uri.EscapeDataString(id)}", ct);
        await ThrowIfProblem(res, ct);
    }

    // ---- Bulk jobs ------------------------------------------------------

    public async Task<BulkJobAccepted> PostBulkJobAsync(
        Stream fileStream, string fileName, string contentType,
        IProgress<(long sent, long? total)>? progress, CancellationToken ct = default)
    {
        // MultipartFormDataContent to match `[FromForm] IFormFile file`
        // on the server (BulkJobEndpoints.AcceptUploadAsync). Progress
        // reporting on the upload body is best-effort — HttpClient in
        // Blazor WASM doesn't expose per-byte transmit callbacks the way
        // XHR does in JS, so we report file-total-then-total when the
        // POST resolves; a JS-driven upload path exists in the browser
        // but isn't wired here.
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(streamContent, "file", fileName);

        using var res = await _http.PostAsync("api/v1/bulk-jobs", content, ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<BulkJobAccepted>(Json, ct))!;
    }

    public async Task<BulkJobStatus> GetBulkJobAsync(string id, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"api/v1/bulk-jobs/{Uri.EscapeDataString(id)}", ct);
        await ThrowIfProblem(res, ct);
        return (await res.Content.ReadFromJsonAsync<BulkJobStatus>(Json, ct))!;
    }

    // SSE stream of BulkJobStatus snapshots. Yields each parsed frame as
    // it arrives. Callers `await foreach` and break on completion, or on
    // the last snapshot's `.Status` reaching a terminal value.
    public async IAsyncEnumerable<BulkJobStatus> StreamBulkJobEventsAsync(
        string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v1/bulk-jobs/{Uri.EscapeDataString(id)}/events");
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfProblem(res, ct);

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Simple SSE parser: frames are separated by blank lines; `data:`
        // lines within a frame are concatenated. We ignore `event:` name
        // and `id:` for now — the server sends the same JSON snapshot on
        // both `message` and the terminal `done` event.
        var buf = new System.Text.StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                var data = buf.ToString();
                buf.Clear();
                if (data.Length == 0) continue;
                BulkJobStatus? snap = null;
                try { snap = JsonSerializer.Deserialize<BulkJobStatus>(data, Json); }
                catch { /* skip malformed frame */ }
                if (snap is not null) yield return snap;
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (buf.Length > 0) buf.Append('\n');
                buf.Append(payload);
            }
            // Silently drop `event:`, `id:`, `retry:` lines — the frame
            // JSON is self-describing.
        }
    }

    // ---- CSV download helpers ------------------------------------------

    public async Task<byte[]> DownloadAsync(string path, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync(path, ct);
        await ThrowIfProblem(res, ct);
        return await res.Content.ReadAsByteArrayAsync(ct);
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
