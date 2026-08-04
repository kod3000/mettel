// Seeded tenants exposed as a client-side dropdown. Values match the
// `client.name` / `client.api_key` rows inserted by `seed/Vocabulary.cs`.
//
// These keys are already documented in the README as public seeded values —
// this is a demo, not a production auth surface. In a real deployment the
// picker becomes a login screen and the key never leaves the server.

export interface Tenant {
    id: string;      // stable id used as localStorage key + React key
    label: string;   // human name in the dropdown
    apiKey: string;  // seeded X-Api-Key
}

export const TENANTS: readonly Tenant[] = [
    { id: "acme",    label: "Acme Telecom",           apiKey: "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme" },
    { id: "beacon",  label: "Beacon Networks",        apiKey: "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_beacon" },
    { id: "cascade", label: "Cascade Communications", apiKey: "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_cascade" },
] as const;

export const DEFAULT_TENANT: Tenant = TENANTS[0];

const STORAGE_KEY = "bruin.tenant";

export function loadTenant(): Tenant {
    if (typeof window === "undefined") return DEFAULT_TENANT;
    const id = window.localStorage.getItem(STORAGE_KEY);
    return TENANTS.find((t) => t.id === id) ?? DEFAULT_TENANT;
}

export function saveTenant(t: Tenant): void {
    if (typeof window === "undefined") return;
    window.localStorage.setItem(STORAGE_KEY, t.id);
}
