import { useEffect, useState } from "react";
import type { components } from "@bruin/api-types";

// Small always-on-screen indicator strip that polls /debug/lsn every 2 s.
// Placement: bottom-left, `position: fixed` so it survives page-level
// layout changes. Toggle persisted to localStorage so a demo operator
// who hid it once doesn't get it back on refresh.
//
// Not a full observability panel — the goal is a single-line "here's
// where primary is, here's where replica is, here's the lag" for
// during-demo storytelling ("watch the replica catch up after a write").

// Wire shape from OpenAPI. Fields with nullable Postgres origin come back
// as `string | null | undefined` in the generated type — handled at render.
type LsnData = components["schemas"]["DebugLsnResponse"];

const STORAGE_KEY = "bruin.lsnBar.visible";
const POLL_MS = 2000;

interface Props { apiKey: string; }

export function LsnStatusBar({ apiKey }: Props) {
    const [visible, setVisible] = useState<boolean>(() => {
        if (typeof window === "undefined") return true;
        return window.localStorage.getItem(STORAGE_KEY) !== "false";
    });
    const [data, setData] = useState<LsnData | null>(null);
    const [err, setErr] = useState<string | null>(null);

    useEffect(() => {
        if (!visible) return;
        let cancelled = false;
        const load = async () => {
            try {
                const res = await fetch("/api/v1/debug/lsn", {
                    headers: { "X-Api-Key": apiKey, "Accept": "application/json" },
                });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                const body = await res.json() as LsnData;
                if (!cancelled) { setData(body); setErr(null); }
            } catch (e: unknown) {
                if (!cancelled) setErr((e as Error).message);
            }
        };
        load();
        const id = window.setInterval(load, POLL_MS);
        return () => { cancelled = true; window.clearInterval(id); };
    }, [apiKey, visible]);

    const toggle = (v: boolean) => {
        window.localStorage.setItem(STORAGE_KEY, v ? "true" : "false");
        setVisible(v);
    };

    if (!visible) {
        return (
            <button
                type="button"
                onClick={() => toggle(true)}
                title="Show LSN status bar"
                className="fixed bottom-2 right-2 z-40 rounded-md border border-slate-300 bg-white/90 px-2 py-1 text-[11px] text-slate-600 shadow hover:bg-white"
            >
                LSN
            </button>
        );
    }

    return (
        <div
            role="status"
            aria-live="off"
            data-testid="lsn-status-bar"
            className="fixed bottom-2 right-2 z-40 flex items-center gap-3 rounded-md border border-slate-300 bg-white/95 px-3 py-1.5 text-[11px] font-mono text-slate-700 shadow"
        >
            {err ? (
                <span className="text-rose-700">LSN: {err}</span>
            ) : data ? (
                <>
                    <span title="Primary WAL LSN">
                        <span className="text-slate-500">primary</span> {data.primary ?? "—"}
                    </span>
                    <span title="Replica replay LSN">
                        <span className="text-slate-500">replica</span> {data.replica ?? "—"}
                    </span>
                    <span
                        title={`byte diff between primary WAL and replica replay (${data.lagSeconds.toFixed(1)}s wall clock)`}
                        className={lagColor(data.lagBytes)}
                    >
                        <span className="text-slate-500">lag</span> {fmtBytes(data.lagBytes)}
                    </span>
                    {!data.reachable && (
                        <span className="text-rose-700 font-semibold">REPLICA DOWN</span>
                    )}
                </>
            ) : (
                <span className="text-slate-500">LSN: loading…</span>
            )}
            <button
                type="button"
                onClick={() => toggle(false)}
                aria-label="Hide LSN status bar"
                title="Hide"
                className="text-slate-400 hover:text-slate-700"
            >
                ✕
            </button>
        </div>
    );
}

function fmtBytes(n: number): string {
    if (n < 1024)          return `${n}B`;
    if (n < 1024 * 1024)   return `${(n / 1024).toFixed(1)}KB`;
    if (n < 1024 ** 3)     return `${(n / 1024 / 1024).toFixed(1)}MB`;
    return `${(n / 1024 / 1024 / 1024).toFixed(2)}GB`;
}

// Green when under 64 KB (well within read-your-own-writes window), amber
// when noticeable, rose when it looks like the replica has stalled.
function lagColor(bytes: number): string {
    if (bytes < 64 * 1024)          return "text-emerald-700";
    if (bytes < 8 * 1024 * 1024)    return "text-amber-700";
    return "text-rose-700 font-semibold";
}
