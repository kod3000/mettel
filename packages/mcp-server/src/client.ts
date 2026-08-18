// Thin typed wrapper around fetch for the Bruin API. Every tool in this
// package funnels through `request()` so:
//   1. The `X-Api-Key` header is applied exactly once per call from a
//      single env-derived source (no leaking keys into tool arguments).
//   2. RFC 7807 ProblemDetails responses are parsed and re-shaped into a
//      structured ApiError with a stable `slug` — the same slug set the
//      SPA/WASM clients switch on. Agents get human-readable text plus
//      machine-readable slug + status.
//   3. Non-JSON responses (204 no-content, CSV template download) are
//      handled without forcing every caller to write the same guard.

export interface BruinConfig {
    baseUrl: string;
    apiKey: string;
}

export class ApiError extends Error {
    readonly status: number;
    readonly slug: string | null;
    readonly problem: unknown;
    readonly fieldErrors: Record<string, string[]> | null;
    constructor(message: string, status: number, slug: string | null, problem: unknown, fieldErrors: Record<string, string[]> | null) {
        super(message);
        this.name = "ApiError";
        this.status = status;
        this.slug = slug;
        this.problem = problem;
        this.fieldErrors = fieldErrors;
    }
}

function slugOf(type: unknown): string | null {
    if (typeof type !== "string" || !type) return null;
    const idx = type.lastIndexOf("/");
    return idx >= 0 ? type.slice(idx + 1) : type;
}

export interface RequestOptions {
    method?: "GET" | "POST" | "PATCH" | "DELETE";
    path: string;
    query?: Record<string, string | number | boolean | string[] | undefined>;
    body?: unknown;
    // multipart form-data upload. Set instead of `body` when uploading a
    // file. `filename` is what shows up in the server's IFormFile.FileName.
    file?: { filename: string; contentType: string; data: Uint8Array };
    // Accept header override — the CSV template + errors CSV need
    // `text/csv` rather than the default `application/json`.
    accept?: string;
}

export interface RawResponse {
    status: number;
    contentType: string;
    text(): Promise<string>;
    bytes(): Promise<Uint8Array>;
}

export function buildQuery(q: RequestOptions["query"]): string {
    if (!q) return "";
    const parts: string[] = [];
    for (const [k, v] of Object.entries(q)) {
        if (v === undefined || v === null) continue;
        if (Array.isArray(v)) {
            for (const item of v) parts.push(`${encodeURIComponent(k)}=${encodeURIComponent(String(item))}`);
        } else {
            parts.push(`${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`);
        }
    }
    return parts.length ? `?${parts.join("&")}` : "";
}

// Core request path. Returns parsed JSON for 2xx application/json responses;
// throws ApiError for 4xx/5xx; returns RawResponse for callers that want to
// stream (used by bulk-upload + CSV downloads).
export async function request<T>(cfg: BruinConfig, opts: RequestOptions): Promise<T> {
    const raw = await requestRaw(cfg, opts);
    if (raw.status === 204) return undefined as T;
    const text = await raw.text();
    // Empty body on a 2xx (rare) — surface as null.
    if (!text) return null as T;
    return JSON.parse(text) as T;
}

export async function requestRaw(cfg: BruinConfig, opts: RequestOptions): Promise<RawResponse> {
    const url = cfg.baseUrl.replace(/\/$/, "") + opts.path + buildQuery(opts.query);
    const headers: Record<string, string> = {
        "X-Api-Key": cfg.apiKey,
        Accept: opts.accept ?? "application/json",
    };

    let body: BodyInit | undefined;
    if (opts.file) {
        const form = new FormData();
        // `Blob` is globally available on Node 18+ and in all runtimes MCP
        // targets, so no polyfill import needed.
        const blob = new Blob([opts.file.data], { type: opts.file.contentType });
        form.append("file", blob, opts.file.filename);
        body = form;
        // Do NOT set Content-Type — fetch adds it with the multipart boundary.
    } else if (opts.body !== undefined) {
        headers["Content-Type"] = "application/json";
        body = JSON.stringify(opts.body);
    }

    const res = await fetch(url, { method: opts.method ?? "GET", headers, body });
    const contentType = res.headers.get("content-type") ?? "";

    if (!res.ok) {
        // Try to parse ProblemDetails; fall back to text if not JSON.
        let problem: unknown = null;
        let message = `HTTP ${res.status} on ${opts.method ?? "GET"} ${opts.path}`;
        let slug: string | null = null;
        let fieldErrors: Record<string, string[]> | null = null;
        try {
            if (contentType.includes("application/problem+json") || contentType.includes("application/json")) {
                problem = await res.json();
                const p = problem as { title?: string; detail?: string; type?: string; errors?: Record<string, string[]> };
                slug = slugOf(p.type);
                fieldErrors = p.errors ?? null;
                const bits = [p.title, p.detail].filter(Boolean).join(" — ");
                if (bits) message = bits;
                if (fieldErrors) {
                    const inline = Object.entries(fieldErrors)
                        .map(([k, v]) => `${k}: ${v.join(", ")}`)
                        .join("; ");
                    message = `${message} (${inline})`;
                }
            } else {
                message = `${message}: ${await res.text()}`;
            }
        } catch {
            /* leave message as-is */
        }
        throw new ApiError(message, res.status, slug, problem, fieldErrors);
    }

    return {
        status: res.status,
        contentType,
        text: () => res.text(),
        bytes: async () => new Uint8Array(await res.arrayBuffer()),
    };
}
