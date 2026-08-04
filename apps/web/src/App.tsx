import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { ApiContext } from "./api/context.js";
import { createClient, inMemoryLsnStore } from "./api/client.js";
import { InventoryGrid } from "./components/InventoryGrid.js";
import { Filters } from "./components/Filters.js";
import { CreateInventoryModal } from "./components/CreateInventoryModal.js";
import { SavedViewsBar } from "./components/SavedViewsBar.js";
import { BulkUploadPanel } from "./components/BulkUploadPanel.js";
import { ClientPicker } from "./components/ClientPicker.js";
import { loadTenant, saveTenant, type Tenant } from "./tenants.js";
import type { ListParams } from "./api/inventory.js";

// Vite bakes VITE_API_KEY at build time if it's set; otherwise the picker
// dropdown drives the runtime key.
const BUILT_IN_OVERRIDE = (import.meta as unknown as {
    env?: { VITE_API_KEY?: string };
}).env?.VITE_API_KEY;

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
                        <ClientPicker value={tenant} onChange={onTenantChange} />
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
                        <InventoryGrid params={params} onParamsChange={setParams} />
                    </div>
                    <CreateInventoryModal open={createOpen} onClose={() => setCreateOpen(false)} />
                </div>
            </QueryClientProvider>
        </ApiContext.Provider>
    );
}
