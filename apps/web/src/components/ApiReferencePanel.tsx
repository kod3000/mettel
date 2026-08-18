// API reference panel — an in-app cheat-sheet so customers (and agents
// acting on their behalf) can call the endpoints directly without
// reverse-engineering the SPA. Everything shown here is derived from the
// same OpenAPI doc the server emits at /openapi.v1.json; the snippets are
// pre-substituted with the current tenant's key + origin so copy-paste
// is one step.
//
// Deliberately NOT a full spec renderer — the OpenAPI JSON link at the top
// covers that. This panel is the "getting started in 30 seconds" surface.

import { useEffect, useMemo, useState } from "react";

interface Props {
    apiKey: string;
    tenantLabel: string;
    onClose: () => void;
}

interface Endpoint {
    method: "GET" | "POST" | "PATCH";
    path: string;
    title: string;
    description: string;
    example: (base: string, key: string) => string;
}

const ENDPOINTS: Endpoint[] = [
    {
        method: "GET",
        path: "/api/v1/inventory",
        title: "List inventory",
        description:
            "Keyset-paginated list. Filters combine with AND. Repeat `status`, `productCategory`, `state` for multi-value filters.",
        example: (b, k) => [
            `curl "${b}/api/v1/inventory?q=fib&status=active&pageSize=100&sort=updatedAt&dir=desc" \\`,
            `  -H "X-Api-Key: ${k}"`,
        ].join("\n"),
    },
    {
        method: "GET",
        path: "/api/v1/inventory/{id}",
        title: "Get single row",
        description: "Reads through the primary — no replica lag on the row you just wrote.",
        example: (b, k) => [
            `curl "${b}/api/v1/inventory/<uuid>" \\`,
            `  -H "X-Api-Key: ${k}"`,
        ].join("\n"),
    },
    {
        method: "POST",
        path: "/api/v1/inventory",
        title: "Create row",
        description: "Response includes `X-Write-LSN` — echo as `X-Min-LSN` on subsequent reads for read-your-own-writes.",
        example: (b, k) => [
            `curl -X POST "${b}/api/v1/inventory" \\`,
            `  -H "X-Api-Key: ${k}" \\`,
            `  -H "Content-Type: application/json" \\`,
            `  -d '{`,
            `    "serviceNumber": "SVC-000001",`,
            `    "productCategory": "voice",`,
            `    "productName": "Business Voice Line",`,
            `    "status": "pending"`,
            `  }'`,
        ].join("\n"),
    },
    {
        method: "PATCH",
        path: "/api/v1/inventory/{id}/status",
        title: "Change status",
        description:
            "Transitions: pending → active, pending → disconnected, active → disconnected. `rowVersion` from GET is required (optimistic concurrency).",
        example: (b, k) => [
            `curl -X PATCH "${b}/api/v1/inventory/<uuid>/status" \\`,
            `  -H "X-Api-Key: ${k}" \\`,
            `  -H "Content-Type: application/json" \\`,
            `  -d '{"status": "active", "rowVersion": 1}'`,
        ].join("\n"),
    },
    {
        method: "POST",
        path: "/api/v1/bulk-jobs",
        title: "Bulk upload (CSV)",
        description:
            "Streams a CSV up to 200MB. Response returns a job id; poll `GET /api/v1/bulk-jobs/{id}` or subscribe to `/events` (SSE) for progress.",
        example: (b, k) => [
            `curl -X POST "${b}/api/v1/bulk-jobs" \\`,
            `  -H "X-Api-Key: ${k}" \\`,
            `  -H "Content-Type: text/csv" \\`,
            `  --data-binary @inventory.csv`,
        ].join("\n"),
    },
];

// Claude Desktop JSON config for wiring the MCP server. Base URL is the
// current origin so the snippet ships runnable per-environment; the API
// key is the caller's current tenant key. Path is intentionally a
// placeholder since it's the reader's local checkout — no way to know.
function mcpConfigSnippet(base: string, apiKey: string): string {
    return JSON.stringify({
        mcpServers: {
            bruin: {
                command: "node",
                args: ["/absolute/path/to/mt-challenge/packages/mcp-server/dist/server.js"],
                env: {
                    BRUIN_API_BASE_URL: base,
                    BRUIN_API_KEY: apiKey,
                },
            },
        },
    }, null, 2);
}

const FILTERS: Array<[string, string]> = [
    ["q",                "Prefix search on service # / product name (e.g. `q=fib` matches Fiber, not Amplifier)."],
    ["status",           "Repeat for OR — `?status=pending&status=active`. Allowed: pending, active, disconnected."],
    ["productCategory",  "Repeat for OR. Allowed: voice, data, wireless, other."],
    ["state",            "US state code, e.g. `state=NY`. Repeatable."],
    ["sort",             "createdAt (default), updatedAt, status, serviceNumber, productName."],
    ["dir",              "asc | desc (default desc)."],
    ["pageSize",         "1–500, default 100."],
    ["cursor",           "Opaque, signed. From `nextCursor` of a prior response. Do not construct by hand."],
];

export function ApiReferencePanel({ apiKey, tenantLabel, onClose }: Props) {
    const [revealed, setRevealed] = useState(false);
    // Pre-substitute the current origin so snippets are directly runnable.
    const base = typeof window !== "undefined" ? window.location.origin : "https://mettel.exercise.dany.codes";

    useEffect(() => {
        const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [onClose]);

    return (
        <div
            className="fixed inset-0 z-30 bg-slate-900/30"
            onClick={onClose}
            data-testid="apiref-backdrop"
        >
            <aside
                onClick={(e) => e.stopPropagation()}
                role="dialog"
                aria-label="API reference"
                data-testid="apiref-panel"
                className="fixed right-0 top-0 bottom-0 w-full sm:w-[560px] bg-white shadow-2xl border-l border-slate-200 flex flex-col"
            >
                <header className="flex items-start gap-3 px-4 py-3 border-b border-slate-200">
                    <div className="flex-1 min-w-0">
                        <div className="text-[11px] uppercase tracking-wide text-slate-500">
                            Direct API access
                        </div>
                        <div className="text-sm font-semibold text-slate-900">
                            {tenantLabel}
                        </div>
                    </div>
                    <a
                        href="/openapi/v1.json"
                        target="_blank"
                        rel="noopener noreferrer"
                        className="text-xs text-indigo-700 hover:underline"
                    >
                        OpenAPI JSON →
                    </a>
                    <button
                        type="button"
                        onClick={onClose}
                        className="rounded-md p-1 text-slate-500 hover:bg-slate-100 hover:text-slate-700"
                        aria-label="Close"
                    >
                        <CloseIcon />
                    </button>
                </header>

                <div className="flex-1 overflow-y-auto px-4 py-4 space-y-5">
                    <section>
                        <div className="text-[11px] uppercase tracking-wide text-slate-500 mb-1">
                            Your API key
                        </div>
                        <div className="flex items-stretch gap-2">
                            <code
                                className="flex-1 font-mono text-xs bg-slate-50 border border-slate-200 rounded px-2 py-1.5 truncate select-all"
                                data-testid="apiref-key"
                            >
                                {revealed ? apiKey : mask(apiKey)}
                            </code>
                            <button
                                type="button"
                                onClick={() => setRevealed((r) => !r)}
                                className="text-xs rounded border border-slate-300 px-2 hover:bg-slate-50"
                            >
                                {revealed ? "Hide" : "Reveal"}
                            </button>
                            <CopyButton text={apiKey} label="Copy" />
                        </div>
                        <p className="text-[11px] text-slate-500 mt-1">
                            Send as <code className="font-mono">X-Api-Key</code> on every request. Demo keys —
                            no rate limiting, no real auth.
                        </p>
                    </section>

                    <section>
                        <div className="text-[11px] uppercase tracking-wide text-slate-500 mb-2">
                            Base URL
                        </div>
                        <code className="font-mono text-xs bg-slate-50 border border-slate-200 rounded px-2 py-1.5 block">
                            {base}
                        </code>
                        <p className="text-[11px] text-slate-500 mt-1">
                            Also reachable at <code className="font-mono">/use/v1/api/*</code> — a stable
                            public alias with the same routes.
                        </p>
                    </section>

                    <section>
                        <div className="flex items-center justify-between mb-2">
                            <div className="text-[11px] uppercase tracking-wide text-slate-500">
                                MCP access for AI agents
                            </div>
                            <a
                                href="https://github.com/kod3000/mt-challenge/tree/main/packages/mcp-server"
                                target="_blank"
                                rel="noopener noreferrer"
                                className="text-[11px] text-indigo-700 hover:underline"
                            >
                                README →
                            </a>
                        </div>
                        <p className="text-xs text-slate-700 leading-relaxed mb-2">
                            Drive the same API from Claude Desktop, Cursor, or any
                            MCP-capable agent — ten tools over stdio, same auth key
                            below. Model-agnostic, no browser required.
                        </p>
                        <div className="text-[11px] uppercase tracking-wide text-slate-500 mb-1">
                            Install
                        </div>
                        <pre className="bg-slate-950 text-slate-100 text-[11.5px] font-mono rounded p-2.5 overflow-x-auto whitespace-pre leading-relaxed">
{`cd packages/mcp-server
npm install && npm run build`}
                        </pre>
                        <div className="text-[11px] uppercase tracking-wide text-slate-500 mt-3 mb-1 flex items-center justify-between">
                            <span>Claude Desktop config</span>
                            <CopyButton
                                text={mcpConfigSnippet(base, apiKey)}
                                label="Copy"
                            />
                        </div>
                        <pre className="bg-slate-950 text-slate-100 text-[11.5px] font-mono rounded p-2.5 overflow-x-auto whitespace-pre leading-relaxed">
{mcpConfigSnippet(base, apiKey)}
                        </pre>
                        <p className="text-[11px] text-slate-500 mt-1">
                            Add to <code className="font-mono">~/Library/Application Support/Claude/claude_desktop_config.json</code>{" "}
                            and restart. The <code className="font-mono">/absolute/path</code> is your local checkout.
                        </p>
                    </section>

                    <section>
                        <div className="text-[11px] uppercase tracking-wide text-slate-500 mb-2">
                            Endpoints
                        </div>
                        <div className="space-y-3">
                            {ENDPOINTS.map((ep) => (
                                <EndpointCard key={ep.path + ep.method} ep={ep} base={base} apiKey={apiKey} />
                            ))}
                        </div>
                    </section>

                    <section>
                        <div className="text-[11px] uppercase tracking-wide text-slate-500 mb-2">
                            List filter parameters
                        </div>
                        <table className="w-full text-xs">
                            <tbody>
                                {FILTERS.map(([name, desc]) => (
                                    <tr key={name} className="border-b border-slate-100 last:border-b-0">
                                        <td className="py-1.5 pr-3 align-top w-32">
                                            <code className="font-mono text-[12px] text-indigo-800">{name}</code>
                                        </td>
                                        <td className="py-1.5 text-slate-700">{desc}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </section>

                    <section>
                        <div className="text-[11px] uppercase tracking-wide text-slate-500 mb-2">
                            Errors
                        </div>
                        <p className="text-xs text-slate-700 leading-relaxed">
                            All errors return RFC 7807 <code className="font-mono">application/problem+json</code>.
                            Switch on the last segment of <code className="font-mono">type</code> — stable slugs
                            include <code className="font-mono">validation-failed</code>,{" "}
                            <code className="font-mono">invalid-status-transition</code>,{" "}
                            <code className="font-mono">concurrency-conflict</code>,{" "}
                            <code className="font-mono">duplicate-service-number</code>,{" "}
                            <code className="font-mono">not-found</code>,{" "}
                            <code className="font-mono">payload-too-large</code>.
                        </p>
                    </section>
                </div>
            </aside>
        </div>
    );
}

function EndpointCard({ ep, base, apiKey }: { ep: Endpoint; base: string; apiKey: string }) {
    const snippet = useMemo(() => ep.example(base, apiKey), [ep, base, apiKey]);
    return (
        <div className="rounded border border-slate-200 bg-white overflow-hidden">
            <div className="flex items-center gap-2 px-3 py-2 bg-slate-50 border-b border-slate-200">
                <MethodBadge method={ep.method} />
                <code className="font-mono text-xs text-slate-800">{ep.path}</code>
                <span className="flex-1" />
                <CopyButton text={snippet} label="Copy" />
            </div>
            <div className="px-3 py-2">
                <div className="text-sm font-medium text-slate-900">{ep.title}</div>
                <p className="text-xs text-slate-600 mt-0.5 leading-relaxed">{ep.description}</p>
                <pre className="mt-2 bg-slate-950 text-slate-100 text-[11.5px] font-mono rounded p-2.5 overflow-x-auto whitespace-pre leading-relaxed">
{snippet}
                </pre>
            </div>
        </div>
    );
}

function MethodBadge({ method }: { method: Endpoint["method"] }) {
    const styles: Record<Endpoint["method"], string> = {
        GET:   "bg-emerald-100 text-emerald-800",
        POST:  "bg-indigo-100 text-indigo-800",
        PATCH: "bg-amber-100 text-amber-800",
    };
    return (
        <span className={`font-mono text-[10.5px] font-semibold px-1.5 py-0.5 rounded ${styles[method]}`}>
            {method}
        </span>
    );
}

function CopyButton({ text, label }: { text: string; label: string }) {
    const [copied, setCopied] = useState(false);
    return (
        <button
            type="button"
            onClick={async () => {
                try {
                    await navigator.clipboard.writeText(text);
                    setCopied(true);
                    setTimeout(() => setCopied(false), 1400);
                } catch { /* clipboard blocked — no-op */ }
            }}
            className="text-xs rounded border border-slate-300 px-2 py-0.5 hover:bg-slate-50"
        >
            {copied ? "Copied ✓" : label}
        </button>
    );
}

function CloseIcon() {
    return (
        <svg viewBox="0 0 20 20" className="h-4 w-4" fill="currentColor" aria-hidden="true">
            <path d="M4.293 4.293a1 1 0 0 1 1.414 0L10 8.586l4.293-4.293a1 1 0 1 1 1.414 1.414L11.414 10l4.293 4.293a1 1 0 0 1-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 0 1-1.414-1.414L8.586 10 4.293 5.707a1 1 0 0 1 0-1.414Z" />
        </svg>
    );
}

function mask(key: string): string {
    if (key.length <= 12) return "•".repeat(key.length);
    return `${key.slice(0, 6)}${"•".repeat(20)}${key.slice(-4)}`;
}
