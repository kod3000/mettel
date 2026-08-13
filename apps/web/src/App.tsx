import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { ApiContext } from "./api/context.js";
import { createClient, inMemoryLsnStore } from "./api/client.js";
import { InventoryGrid } from "./components/InventoryGrid.js";
// PixiJS is ~150KB gzipped — lazy-imported so users on the DOM grid never
// pay the download cost. The Suspense fallback is a plain skeleton because
// the chunk is small enough (over a warm connection) that a heavier
// spinner would be visible longer than the load itself.
const InventoryGridGpu = lazy(() =>
    import("./components/InventoryGridGpu.js").then((m) => ({ default: m.InventoryGridGpu })),
);
import { Filters } from "./components/Filters.js";
import { CreateInventoryModal } from "./components/CreateInventoryModal.js";
import { SavedViewsBar } from "./components/SavedViewsBar.js";
import { BulkUploadPanel } from "./components/BulkUploadPanel.js";
import { ClientPicker } from "./components/ClientPicker.js";
import { RowDetailDrawer } from "./components/RowDetailDrawer.js";
import { ApiReferencePanel } from "./components/ApiReferencePanel.js";
import { Toaster } from "./components/Toaster.js";
import { loadTenant, saveTenant, type Tenant } from "./tenants.js";
import type { InventoryRow, ListParams } from "./api/inventory.js";

// Vite bakes VITE_API_KEY at build time if it's set; otherwise the picker
// dropdown drives the runtime key.
const BUILT_IN_OVERRIDE = (import.meta as unknown as {
    env?: { VITE_API_KEY?: string };
}).env?.VITE_API_KEY;

// Persist the GPU-UI toggle so a user who turned it off on a slow machine
// isn't surprised by hover animations on the next visit. Mirror the same
// storage/load shape `tenants.ts` uses for its picker preference.
const GPU_STORAGE_KEY = "bruin.gpuHover";
function loadGpuHover(): boolean {
    if (typeof window === "undefined") return false;
    const v = window.localStorage.getItem(GPU_STORAGE_KEY);
    // Default OFF: GPU mode now swaps the DOM grid body for a PixiJS canvas,
    // which is inaccessible to screen readers. Opt-in only. Persisted after
    // the user flips it on.
    return v === "true";
}
function saveGpuHover(on: boolean): void {
    if (typeof window === "undefined") return;
    window.localStorage.setItem(GPU_STORAGE_KEY, on ? "true" : "false");
}

export default function App() {
    const [tenant, setTenant] = useState<Tenant>(() => loadTenant());
    return <AppShell tenant={tenant} onTenantChange={(t) => { saveTenant(t); setTenant(t); }} />;
}

// AppShell is keyed on the tenant so switching client remounts the whole
// tree — every hook state, every query cache, every LSN watermark starts
// fresh. Simpler and safer than reaching into caches to purge tenant-A rows.
function AppShell({ tenant, onTenantChange }: { tenant: Tenant; onTenantChange: (t: Tenant) => void; }) {
    const [params, setParams] = useState<ListParams>({ pageSize: 100 });
    const [createOpen, setCreateOpen] = useState(false);
    const [selectedRow, setSelectedRow] = useState<InventoryRow | null>(null);
    const [apiRefOpen, setApiRefOpen] = useState(false);
    // GPU-UI mode: on by default so first-time users see the animation;
    // toggle off if the machine is slow or the effect is distracting.
    // Preference persists across refresh via localStorage.
    const [gpuHover, setGpuHover] = useState<boolean>(() => loadGpuHover());
    const setGpuHoverPersistent = (v: boolean) => { saveGpuHover(v); setGpuHover(v); };

    // Confirm-before-leave. Fires on tab close, refresh, URL change, back
    // button, and cross-origin link clicks. Browsers ignore any custom
    // message and show their own text; the empty `returnValue` assignment
    // is the trigger. Modern browsers also gate this on a "user activation"
    // — the prompt only appears once the user has interacted with the page,
    // so a first-load reload before any click is silent (which is what we
    // want; nothing to lose yet).
    useEffect(() => {
        const onBeforeUnload = (e: BeforeUnloadEvent) => {
            e.preventDefault();
            e.returnValue = "";
        };
        window.addEventListener("beforeunload", onBeforeUnload);
        return () => window.removeEventListener("beforeunload", onBeforeUnload);
    }, []);

    const apiKey = BUILT_IN_OVERRIDE ?? tenant.apiKey;

    const client = useMemo(() => createClient({
        apiKey,
        tenantId: apiKey,
        lsnStore: inMemoryLsnStore(),
    }), [apiKey]);

    const qc = useMemo(() => new QueryClient({
        defaultOptions: { queries: { staleTime: 5_000, gcTime: 60_000, retry: 1 } },
    }), []);

    return (
        <ApiContext.Provider value={client}>
            <QueryClientProvider client={qc}>
                <div key={tenant.id} className="h-screen flex flex-col bg-slate-50 text-slate-900">
                    <header className="flex items-center gap-3 border-b border-slate-200 bg-white px-4 py-3 shadow-sm">
                        <div className="flex-1">
                            <h1 className="text-base font-semibold tracking-tight">Bruin Inventory Grid</h1>
                            <p className="text-xs text-slate-500">MetTel Bruin Platform — fun-times</p>
                        </div>
                        <GpuToggle value={gpuHover} onChange={setGpuHoverPersistent} />
                        <ClientPicker value={tenant} onChange={onTenantChange} />
                        <button
                            type="button"
                            onClick={() => setApiRefOpen(true)}
                            data-testid="btn-api-ref"
                            className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50"
                        >
                            API
                        </button>
                        <button
                            type="button"
                            onClick={() => setCreateOpen(true)}
                            data-testid="btn-new"
                            className="rounded-md bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                        >
                            + New
                        </button>
                    </header>
                    <SavedViewsBar params={params} onApply={setParams} />
                    <BulkUploadPanel apiKey={apiKey} />
                    <Filters value={params} onChange={setParams} />
                    <div className="flex-1 min-h-0">
                        {gpuHover ? (
                            <Suspense
                                fallback={
                                    <div className="h-full flex items-center justify-center text-sm text-slate-500">
                                        Loading GPU renderer…
                                    </div>
                                }
                            >
                                <InventoryGridGpu
                                    params={params}
                                    onParamsChange={setParams}
                                    onRowSelect={setSelectedRow}
                                />
                            </Suspense>
                        ) : (
                            <InventoryGrid
                                params={params}
                                onParamsChange={setParams}
                                onRowSelect={setSelectedRow}
                                gpuHover={false}
                            />
                        )}
                    </div>
                    <CreateInventoryModal open={createOpen} onClose={() => setCreateOpen(false)} />
                    {selectedRow && (
                        <RowDetailDrawer
                            id={selectedRow.id}
                            onClose={() => setSelectedRow(null)}
                        />
                    )}
                    {apiRefOpen && (
                        <ApiReferencePanel
                            apiKey={apiKey}
                            tenantLabel={tenant.label}
                            onClose={() => setApiRefOpen(false)}
                        />
                    )}
                    <Toaster />
                </div>
            </QueryClientProvider>
        </ApiContext.Provider>
    );
}

function GpuToggle({ value, onChange }: { value: boolean; onChange: (v: boolean) => void }) {
    return (
        <label
            className="flex items-center gap-2 text-xs text-slate-600 cursor-pointer select-none"
            title="Render the grid body on the GPU via PixiJS (WebGPU/WebGL). Turn off to use the DOM grid — required for screen readers and full keyboard nav."
        >
            <span className="text-slate-500">GPU UI</span>
            <button
                type="button"
                role="switch"
                aria-checked={value}
                onClick={() => onChange(!value)}
                data-testid="gpu-toggle"
                className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors ${
                    value ? "bg-indigo-600" : "bg-slate-300"
                }`}
            >
                <span
                    className={`inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform ${
                        value ? "translate-x-4" : "translate-x-0.5"
                    }`}
                />
            </button>
        </label>
    );
}
