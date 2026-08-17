import { useEffect, useState } from "react";

// Developer smoke checklist. Modal-style overlay with a list of scenarios
// the operator can Run individually or as a batch. Each hits the live API
// with the current tenant + role and reports pass/fail with the response
// body. Useful right after a fresh clone or during a demo to prove every
// piece of the CRUD + search + bulk path is up.
//
// Admin-only: the button that opens this panel is gated in App.tsx behind
// `canDelete` (== admin) because several scenarios exercise DELETE.

interface Props {
    apiKey: string;
    onClose: () => void;
}

type Status = "idle" | "running" | "pass" | "fail";

interface Scenario {
    id: string;
    name: string;
    description: string;
    run: (ctx: RunContext) => Promise<{ ok: boolean; detail: string }>;
}

interface RunContext {
    apiKey: string;
    stash: Record<string, string>; // pass values between scenarios (e.g. created row id)
}

// Declarative scenario loaded from public/smoke-scenarios.json. Adding
// one only requires editing that JSON + redeploying the static bundle —
// no code change. Rich scenarios that need to stash or do multi-step
// logic still live in the TS list above.
interface JsonScenario {
    id: string;
    name: string;
    description: string;
    method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
    path: string;
    body?: unknown;
    expectStatus?: number;
    expectContains?: string;
}

// Utility fetch with the current key + JSON accept.
async function api(apiKey: string, path: string, init?: RequestInit): Promise<Response> {
    return await fetch(path, {
        ...init,
        headers: {
            "X-Api-Key": apiKey,
            "Accept": "application/json",
            ...(init?.headers ?? {}),
        },
    });
}

const SCENARIOS: Scenario[] = [
    {
        id: "me",
        name: "GET /me",
        description: "Auth + role resolution",
        run: async ({ apiKey }) => {
            const res = await api(apiKey, "/api/v1/me");
            if (!res.ok) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { clientId?: string; role?: string };
            return { ok: !!body.clientId && !!body.role, detail: `role=${body.role} clientId=${body.clientId?.slice(0, 8)}…` };
        },
    },
    {
        id: "list",
        name: "GET /inventory?pageSize=1",
        description: "Read path via list handler",
        run: async ({ apiKey }) => {
            const res = await api(apiKey, "/api/v1/inventory?pageSize=1");
            if (!res.ok) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { rows: unknown[]; totalEstimate: { value: number } };
            return { ok: Array.isArray(body.rows), detail: `${body.rows.length} row · ~${body.totalEstimate?.value?.toLocaleString?.() ?? "?"} total` };
        },
    },
    {
        id: "debug-lsn",
        name: "GET /debug/lsn",
        description: "Primary + replica LSN visibility",
        run: async ({ apiKey }) => {
            const res = await api(apiKey, "/api/v1/debug/lsn");
            if (!res.ok) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { primary: string; replica: string; lagBytes: number };
            return { ok: !!body.primary, detail: `p=${body.primary} r=${body.replica} lag=${body.lagBytes}B` };
        },
    },
    {
        id: "search",
        name: "Search 'fiber'",
        description: "tsvector path exercise",
        run: async ({ apiKey }) => {
            const res = await api(apiKey, "/api/v1/inventory?q=fiber&pageSize=5");
            if (!res.ok) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { rows: unknown[] };
            return { ok: body.rows.length > 0, detail: `${body.rows.length} matches` };
        },
    },
    {
        id: "search-narrowed",
        name: "Search 'boston' fields=city",
        description: "Fine-grained scope narrowing",
        run: async ({ apiKey }) => {
            const res = await api(apiKey, "/api/v1/inventory?q=boston&fields=city&pageSize=5");
            if (!res.ok) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { rows: { city?: string }[] };
            const allBoston = body.rows.every((r) => (r.city ?? "").toLowerCase() === "boston");
            return { ok: allBoston && body.rows.length > 0, detail: `${body.rows.length} rows, all city=Boston: ${allBoston}` };
        },
    },
    {
        id: "create",
        name: "POST /inventory (create)",
        description: "Round-trip create + stash id",
        run: async ({ apiKey, stash }) => {
            const sn = `DEV-SMOKE-${Date.now()}`;
            const res = await api(apiKey, "/api/v1/inventory", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    serviceNumber: sn, productCategory: "voice",
                    productName: "Dev smoke row", status: "pending",
                }),
            });
            if (!res.ok) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { id: string; serviceNumber: string; rowVersion: number };
            stash.smokeId = body.id;
            stash.smokeRowVersion = String(body.rowVersion);
            return { ok: true, detail: `id=${body.id.slice(0, 8)}… sn=${body.serviceNumber}` };
        },
    },
    {
        id: "patch",
        name: "PATCH /inventory/{id}",
        description: "Full-field patch on the created row",
        run: async ({ apiKey, stash }) => {
            if (!stash.smokeId) return { ok: false, detail: "no smoke row id (run create first)" };
            const rv = Number(stash.smokeRowVersion ?? "1");
            const res = await api(apiKey, `/api/v1/inventory/${stash.smokeId}`, {
                method: "PATCH",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ rowVersion: rv, notes: "patched by dev smoke" }),
            });
            if (!res.ok) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { rowVersion: number; notes?: string };
            stash.smokeRowVersion = String(body.rowVersion);
            return { ok: body.notes === "patched by dev smoke", detail: `notes updated · rv ${rv} → ${body.rowVersion}` };
        },
    },
    {
        id: "delete",
        name: "DELETE /inventory/{id} (soft)",
        description: "Admin-only soft delete of the smoke row",
        run: async ({ apiKey, stash }) => {
            if (!stash.smokeId) return { ok: false, detail: "no smoke row id" };
            const res = await api(apiKey, `/api/v1/inventory/${stash.smokeId}`, { method: "DELETE" });
            if (res.status !== 204) return { ok: false, detail: `HTTP ${res.status}` };
            // Confirm GET returns 404
            const check = await api(apiKey, `/api/v1/inventory/${stash.smokeId}`);
            const gone = check.status === 404;
            return { ok: gone, detail: gone ? "row soft-deleted, GET 404" : `GET after delete = ${check.status}` };
        },
    },
    {
        id: "bulk-good",
        name: "POST /bulk-jobs (2-row CSV)",
        description: "Multipart upload path",
        run: async ({ apiKey, stash }) => {
            const sn1 = `DEV-BULK-A-${Date.now()}`;
            const sn2 = `DEV-BULK-B-${Date.now()}`;
            const csv =
                "serviceNumber,productCategory,productName,status\n" +
                `${sn1},voice,Bulk row A,pending\n` +
                `${sn2},data,Bulk row B,active\n`;
            const form = new FormData();
            form.append("file", new Blob([csv], { type: "text/csv" }), "dev-smoke.csv");
            const res = await api(apiKey, "/api/v1/bulk-jobs", { method: "POST", body: form });
            if (res.status !== 202) return { ok: false, detail: `HTTP ${res.status}` };
            const body = await res.json() as { jobId: string };
            stash.bulkJobId = body.jobId;
            return { ok: true, detail: `accepted jobId=${body.jobId.slice(0, 8)}…` };
        },
    },
    {
        id: "bulk-bad-header",
        name: "POST /bulk-jobs (bad header)",
        description: "Expect job.status=failed with reason",
        run: async ({ apiKey }) => {
            const csv = "this,is,not,valid\nx,y,z,w\n";
            const form = new FormData();
            form.append("file", new Blob([csv], { type: "text/csv" }), "bad-header.csv");
            const res = await api(apiKey, "/api/v1/bulk-jobs", { method: "POST", body: form });
            if (res.status !== 202) return { ok: false, detail: `POST HTTP ${res.status}` };
            const { jobId } = await res.json() as { jobId: string };
            // Poll up to ~15 s for terminal status — the worker may be
            // draining another job when we submit, so give it headroom.
            for (let i = 0; i < 20; i++) {
                await new Promise((r) => setTimeout(r, 750));
                const s = await api(apiKey, `/api/v1/bulk-jobs/${jobId}`);
                if (!s.ok) continue;
                const snap = await s.json() as { status: string };
                if (snap.status === "failed") {
                    const e = await api(apiKey, `/api/v1/bulk-jobs/${jobId}/errors`);
                    const errs = await e.json() as { errors: { reason: string }[] };
                    const first = errs.errors?.[0]?.reason ?? "";
                    return { ok: /header/i.test(first), detail: `failed: ${first.slice(0, 80)}` };
                }
                if (snap.status === "completed" || snap.status === "completedWithErrors") {
                    return { ok: false, detail: `unexpected terminal: ${snap.status}` };
                }
            }
            return { ok: false, detail: "timeout waiting for terminal status" };
        },
    },
    {
        id: "reader-403",
        name: "Reader key gets 403 on POST",
        description: "Role enforcement smoke",
        run: async () => {
            // Derive the reader key from the admin key by suffix convention.
            const admin = "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme";
            const readerKey = `${admin}_reader`;
            const res = await fetch("/api/v1/inventory", {
                method: "POST",
                headers: { "X-Api-Key": readerKey, "Content-Type": "application/json" },
                body: JSON.stringify({
                    serviceNumber: "DEV-READER-SHOULD-FAIL",
                    productCategory: "voice", productName: "should not stick", status: "pending",
                }),
            });
            return { ok: res.status === 403, detail: `HTTP ${res.status} (want 403)` };
        },
    },
];

// Turn a JSON scenario config into the same Scenario shape the built-in
// list uses. Kept out of the module top-level so it captures apiKey via
// closure at the point of use.
function scenarioFromJson(js: JsonScenario): Scenario {
    return {
        id: js.id,
        name: js.name,
        description: js.description,
        run: async ({ apiKey }) => {
            const res = await api(apiKey, js.path, {
                method: js.method ?? "GET",
                headers: js.body !== undefined ? { "Content-Type": "application/json" } : undefined,
                body: js.body !== undefined ? JSON.stringify(js.body) : undefined,
            });
            const want = js.expectStatus ?? 200;
            if (res.status !== want) return { ok: false, detail: `HTTP ${res.status} (want ${want})` };
            if (js.expectContains) {
                const text = await res.text();
                if (!text.includes(js.expectContains)) {
                    return { ok: false, detail: `response missing "${js.expectContains}"` };
                }
                return { ok: true, detail: `HTTP ${res.status} + contains "${js.expectContains}"` };
            }
            return { ok: true, detail: `HTTP ${res.status}` };
        },
    };
}

export function DevPanel({ apiKey, onClose }: Props) {
    const [results, setResults] = useState<Record<string, { status: Status; detail: string; ms: number }>>({});
    const [running, setRunning] = useState(false);
    const [extra, setExtra] = useState<Scenario[]>([]);

    // Load /smoke-scenarios.json once on mount. Failures are silent —
    // the JSON file is optional; the built-in TS scenarios keep working.
    useEffect(() => {
        let cancelled = false;
        (async () => {
            try {
                const res = await fetch("/smoke-scenarios.json", { cache: "no-cache" });
                if (!res.ok) return;
                const body = await res.json() as { scenarios?: JsonScenario[] };
                if (!cancelled && Array.isArray(body.scenarios)) {
                    setExtra(body.scenarios.map(scenarioFromJson));
                }
            } catch { /* file missing or malformed — ignore */ }
        })();
        return () => { cancelled = true; };
    }, []);

    const scenarios = SCENARIOS.concat(extra);

    async function runOne(sc: Scenario, stash: Record<string, string>) {
        setResults((r) => ({ ...r, [sc.id]: { status: "running", detail: "…", ms: 0 } }));
        const start = performance.now();
        try {
            const r = await sc.run({ apiKey, stash });
            const ms = Math.round(performance.now() - start);
            setResults((prev) => ({ ...prev, [sc.id]: { status: r.ok ? "pass" : "fail", detail: r.detail, ms } }));
        } catch (e: unknown) {
            const ms = Math.round(performance.now() - start);
            setResults((prev) => ({ ...prev, [sc.id]: { status: "fail", detail: (e as Error).message, ms } }));
        }
    }

    async function runAll() {
        setRunning(true);
        setResults({});
        const stash: Record<string, string> = {};
        for (const sc of scenarios) await runOne(sc, stash);
        setRunning(false);
    }

    const passCount = Object.values(results).filter((r) => r.status === "pass").length;
    const failCount = Object.values(results).filter((r) => r.status === "fail").length;

    return (
        <div
            className="fixed inset-0 z-40 flex items-center justify-center bg-slate-900/50 p-4"
            onClick={onClose}
        >
            <div
                onClick={(e) => e.stopPropagation()}
                role="dialog"
                aria-label="Developer smoke checklist"
                className="max-h-[85vh] w-full max-w-3xl overflow-hidden rounded-lg bg-white shadow-2xl border border-slate-200 flex flex-col"
            >
                <header className="flex items-center gap-3 border-b border-slate-200 px-4 py-3">
                    <div className="flex-1">
                        <h2 className="text-sm font-semibold text-slate-900">Developer smoke checklist</h2>
                        <p className="text-xs text-slate-500">
                            Runs against the live API with the current tenant + role key. Use after a
                            fresh clone or before a demo to verify each path.
                        </p>
                    </div>
                    <span className="text-xs tabular-nums text-slate-600">
                        {passCount > 0 && <span className="text-emerald-700">✓ {passCount}</span>}
                        {failCount > 0 && <span className="ml-2 text-rose-700">✗ {failCount}</span>}
                    </span>
                    <button
                        type="button"
                        onClick={runAll}
                        disabled={running}
                        className="rounded-md bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white shadow-sm hover:bg-indigo-500 disabled:opacity-60"
                    >
                        {running ? "Running…" : "Run all"}
                    </button>
                    <button
                        type="button"
                        onClick={onClose}
                        className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50"
                    >
                        Close
                    </button>
                </header>

                <div className="overflow-y-auto">
                    <table className="w-full text-xs">
                        <thead className="sticky top-0 bg-slate-50 text-left">
                            <tr>
                                <th className="px-3 py-2 w-8"></th>
                                <th className="px-3 py-2 w-56">Scenario</th>
                                <th className="px-3 py-2">Description / last result</th>
                                <th className="px-3 py-2 w-16 text-right">ms</th>
                                <th className="px-3 py-2 w-14"></th>
                            </tr>
                        </thead>
                        <tbody>
                            {scenarios.map((sc) => {
                                const r = results[sc.id];
                                return (
                                    <tr key={sc.id} className="border-t border-slate-100">
                                        <td className="px-3 py-2">
                                            <StatusChip status={r?.status ?? "idle"} />
                                        </td>
                                        <td className="px-3 py-2 font-mono text-[11px] text-slate-800">
                                            {sc.name}
                                        </td>
                                        <td className="px-3 py-2 text-slate-600">
                                            {r ? r.detail : sc.description}
                                        </td>
                                        <td className="px-3 py-2 text-right tabular-nums text-slate-500">
                                            {r?.ms ? r.ms : ""}
                                        </td>
                                        <td className="px-3 py-2 text-right">
                                            <button
                                                type="button"
                                                onClick={() => runOne(sc, {})}
                                                disabled={running || r?.status === "running"}
                                                className="rounded border border-slate-300 bg-white px-2 py-0.5 text-[11px] font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50"
                                            >
                                                Run
                                            </button>
                                        </td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
            </div>
        </div>
    );
}

function StatusChip({ status }: { status: Status }) {
    const cls = status === "pass"    ? "bg-emerald-100 text-emerald-800"
              : status === "fail"    ? "bg-rose-100 text-rose-800"
              : status === "running" ? "bg-amber-100 text-amber-800 animate-pulse"
              :                        "bg-slate-100 text-slate-500";
    const glyph = status === "pass" ? "✓" : status === "fail" ? "✗" : status === "running" ? "…" : "·";
    return (
        <span className={`inline-flex h-5 w-5 items-center justify-center rounded-full text-[11px] font-semibold ${cls}`}>
            {glyph}
        </span>
    );
}
