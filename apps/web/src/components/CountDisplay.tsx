import type { components } from "@bruin/api-types";

type CountEnvelope = components["schemas"]["CountEnvelope"];

interface Props {
    totalEstimate: CountEnvelope | null | undefined;
    filteredCount: CountEnvelope | null | undefined;
    loaded: number;
    // Server-side query time for the last page fetched (ms). Sourced from
    // the API's `tookMs` field — measured before serialisation, so it's
    // pure DB + handler cost, no network.
    lastServerMs: number | null | undefined;
}

export function CountDisplay({ totalEstimate, filteredCount, loaded, lastServerMs }: Props) {
    return (
        <div className="flex items-center gap-4 px-4 py-2 text-xs text-slate-600 bg-white border-b border-slate-200">
            <span>
                Loaded <strong className="tabular-nums text-slate-900">{loaded.toLocaleString()}</strong>
            </span>
            {filteredCount && (
                <span>
                    Matches <strong className="tabular-nums text-slate-900">{formatCount(filteredCount)}</strong>
                </span>
            )}
            {totalEstimate && !filteredCount && (
                <span title="pg_class.reltuples — table-wide estimate across all tenants; apply a filter for a tenant-scoped count.">
                    Table total <strong className="tabular-nums text-slate-900">{formatCount(totalEstimate)}</strong>
                </span>
            )}
            {lastServerMs != null && (
                <span
                    title="Server-side query time for the last page (Postgres + handler; excludes network)."
                    className={serverTimeClass(lastServerMs)}
                >
                    Response <strong className="tabular-nums">{formatMs(lastServerMs)}</strong>
                </span>
            )}
        </div>
    );
}

function formatMs(ms: number): string {
    if (ms < 1) return "<1 ms";
    if (ms < 1000) return `${Math.round(ms)} ms`;
    return `${(ms / 1000).toFixed(2)} s`;
}

function serverTimeClass(ms: number): string {
    // Green under budget, amber slow, rose alarming — the Phase 4 gate is
    // p95 ≤ 500 ms so 500 is the amber boundary and 1500 is red.
    if (ms < 500)  return "text-emerald-700";
    if (ms < 1500) return "text-amber-700";
    return "text-rose-700";
}

function formatCount(c: CountEnvelope | null | undefined): string {
    if (!c) return "0";
    const n = Number(c.value ?? 0);
    switch (c.kind) {
        case "atLeast":     return `${n.toLocaleString()}+`;
        case "approximate": return `~${abbreviate(n)}`;
        case "exact":
        default:            return n.toLocaleString();
    }
}

function abbreviate(n: number): string {
    if (n >= 1_000_000_000) return `${(n / 1_000_000_000).toFixed(1)}B`;
    if (n >= 1_000_000)     return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 1_000)         return `${(n / 1_000).toFixed(1)}K`;
    return `${n}`;
}
