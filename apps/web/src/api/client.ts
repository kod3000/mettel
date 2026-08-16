// Thin typed fetch client. Everything is:
//   - Typed via @bruin/api-types (never hand-authored types here)
//   - X-Api-Key header injected
//   - X-Min-LSN echoed per tenant so read-your-own-writes stays correct
//   - X-Write-LSN captured from mutation responses and stashed in the store
//   - AbortSignal-aware
//   - ProblemDetails responses parsed into a typed error union
//
// Any type imported here must exist in @bruin/api-types — bug otherwise.

import type { Problem, ProblemSlug } from "@bruin/api-types";
import { slugOf } from "@bruin/api-types";

export interface ApiClientOptions {
    baseUrl?: string;
    apiKey: string;
    tenantId: string;      // opaque to us, keyed only for the LSN store
    lsnStore?: LsnStore;   // pluggable so tests can pass an in-memory one
    fetchImpl?: typeof fetch;
}

// Per-tenant LSN watermark. Reads echo it via X-Min-LSN, writes update it
// via X-Write-LSN on the response. In-memory implementation is fine for the
// grid — refresh on the next tab is acceptable.
export interface LsnStore {
    get(tenantId: string): string | undefined;
    set(tenantId: string, lsn: string): void;
}

export const inMemoryLsnStore = (): LsnStore => {
    const map = new Map<string, string>();
    return {
        get: (t) => map.get(t),
        set: (t, lsn) => { map.set(t, lsn); },
    };
};

// The typed error we surface to app code. Callers switch on `.slug`.
export class ApiError extends Error {
    readonly status: number;
    readonly slug: ProblemSlug | null;
    readonly problem: Problem;
    constructor(problem: Problem) {
        super(problem.title ?? `HTTP ${problem.status ?? "?"}`);
        this.problem = problem;
        this.status = problem.status ?? 0;
        this.slug = slugOf(problem);
    }
    isSlug<S extends ProblemSlug>(s: S): boolean { return this.slug === s; }
}

export interface RequestOptions {
    method?: "GET" | "POST" | "PATCH" | "PUT" | "DELETE";
    query?: Record<string, string | number | string[] | undefined>;
    body?: unknown;
    signal?: AbortSignal;
    // Skip LSN injection — used by /health/* and /metrics style paths.
    skipLsn?: boolean;
}

export interface ApiClient {
    request<T>(path: string, opts?: RequestOptions): Promise<T>;
    get<T>(path: string, opts?: Omit<RequestOptions, "method" | "body">): Promise<T>;
    post<T>(path: string, body: unknown, opts?: Omit<RequestOptions, "method" | "body">): Promise<T>;
    patch<T>(path: string, body: unknown, opts?: Omit<RequestOptions, "method" | "body">): Promise<T>;
    del<T = void>(path: string, opts?: Omit<RequestOptions, "method" | "body">): Promise<T>;
    apiKey: string;
}

export function createClient(opts: ApiClientOptions): ApiClient {
    const baseUrl = (opts.baseUrl ?? inferBaseUrl()).replace(/\/+$/, "");
    const store = opts.lsnStore ?? inMemoryLsnStore();
    const fetchImpl = opts.fetchImpl ?? fetch;

    async function request<T>(path: string, o: RequestOptions = {}): Promise<T> {
        const url = buildUrl(baseUrl, path, o.query);
        const headers: HeadersInit = {
            "X-Api-Key": opts.apiKey,
            ...(o.body !== undefined ? { "Content-Type": "application/json" } : {}),
        };
        if (!o.skipLsn) {
            const min = store.get(opts.tenantId);
            if (min) (headers as Record<string, string>)["X-Min-LSN"] = min;
        }

        const res = await fetchImpl(url, {
            method: o.method ?? "GET",
            headers,
            body: o.body === undefined ? undefined : JSON.stringify(o.body),
            signal: o.signal,
        });

        // Capture the LSN watermark from mutations, always — before any
        // early return / error throw so a 409 that still bumped the row
        // doesn't get lost.
        const writeLsn = res.headers.get("X-Write-LSN");
        if (writeLsn) store.set(opts.tenantId, writeLsn);

        if (!res.ok) {
            // ProblemDetails responses use `application/problem+json`; be
            // permissive about servers that emit `application/json` too.
            let body: unknown;
            try { body = await res.json(); } catch { body = { title: res.statusText, status: res.status, type: "" }; }
            throw new ApiError(body as Problem);
        }

        // 204 No Content — return undefined-as-T. Callers of 204 endpoints
        // should type T as void; we don't enforce because the grid doesn't
        // hit any 204 paths today.
        if (res.status === 204) return undefined as unknown as T;
        return await res.json() as T;
    }

    return {
        request,
        get:   (p, o) => request(p,     { ...o, method: "GET" }),
        post:  (p, b, o) => request(p, { ...o, method: "POST", body: b }),
        patch: (p, b, o) => request(p, { ...o, method: "PATCH", body: b }),
        del:   (p, o) => request(p,     { ...o, method: "DELETE" }),
        apiKey: opts.apiKey,
    };
}

function buildUrl(base: string, path: string, query?: RequestOptions["query"]): string {
    const url = new URL(path.startsWith("/") ? path : "/" + path, base + "/");
    if (query) {
        for (const [k, v] of Object.entries(query)) {
            if (v === undefined) continue;
            if (Array.isArray(v)) for (const one of v) url.searchParams.append(k, String(one));
            else url.searchParams.set(k, String(v));
        }
    }
    return url.toString();
}

function inferBaseUrl(): string {
    // Vite injects import.meta.env at build time; guard for non-Vite callers.
    const envBase = (typeof import.meta !== "undefined" && (import.meta as unknown as {
        env?: { VITE_API_BASE?: string };
    }).env?.VITE_API_BASE) || "";
    if (envBase) return envBase;
    // Fall back to the current window origin. `new URL(path, base)` needs
    // an absolute base — a bare "" or "/" throws "Invalid base URL". The
    // SPA is served from the same origin as the API (nginx proxies
    // /api/v1/* on the deploy host and Vite proxies in dev), so the
    // origin is a safe default. Tests using a jsdom polyfill without
    // window still get a workable value.
    if (typeof globalThis !== "undefined"
        && (globalThis as { location?: { origin?: string } }).location?.origin) {
        return (globalThis as { location: { origin: string } }).location.origin;
    }
    return "http://localhost";
}
