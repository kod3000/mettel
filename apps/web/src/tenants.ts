// Seeded tenants exposed as a client-side dropdown. Values match the
// `client.name` / `client.api_key` rows inserted by `seed/Vocabulary.cs`.
//
// A tenant now carries THREE keys — one per role. The demo picker lets the
// operator flip role (admin/worker/reader) without editing localStorage;
// suffixes match the A1 migration seed (`_worker`, `_reader`).
//
// These keys are already documented in the README as public seeded values —
// this is a demo, not a production auth surface. In a real deployment the
// picker becomes a login screen and the key never leaves the server.

export type Role = "admin" | "worker" | "reader";
export const ROLES: readonly Role[] = ["admin", "worker", "reader"] as const;

export interface Tenant {
    id: string;      // stable id used as localStorage key + React key
    label: string;   // human name in the dropdown
    apiKey: string;  // seeded X-Api-Key for the admin role
}

export const TENANTS: readonly Tenant[] = [
    { id: "acme",    label: "Acme Telecom",           apiKey: "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme" },
    { id: "beacon",  label: "Beacon Networks",        apiKey: "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_beacon" },
    { id: "cascade", label: "Cascade Communications", apiKey: "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_cascade" },
] as const;

export const DEFAULT_TENANT: Tenant = TENANTS[0];
export const DEFAULT_ROLE: Role = "admin";

// Suffix convention matches the migration seed:
//   admin  → <base>
//   worker → <base>_worker
//   reader → <base>_reader
export function apiKeyForRole(tenant: Tenant, role: Role): string {
    return role === "admin" ? tenant.apiKey : `${tenant.apiKey}_${role}`;
}

const TENANT_STORAGE_KEY = "bruin.tenant";
const ROLE_STORAGE_KEY   = "bruin.role";
const CUSTOM_KEY_STORAGE_KEY = "bruin.customKey";

// A user-supplied X-Api-Key that overrides the built-in Client + Role
// picker. Populated by pasting a key issued by the identity console
// (auth.mettel.exercise.dany.codes). Empty / null means "use the demo
// picker". The API's IdentityFallback resolver on the grid recognises
// any key that /resolve accepts, so this is enough to sign in with a
// real tenant without editing localStorage by hand.
export function loadCustomKey(): string | null {
    if (typeof window === "undefined") return null;
    const v = window.localStorage.getItem(CUSTOM_KEY_STORAGE_KEY);
    return v && v.trim().length > 0 ? v : null;
}

export function saveCustomKey(key: string | null): void {
    if (typeof window === "undefined") return;
    if (!key || key.trim().length === 0) {
        window.localStorage.removeItem(CUSTOM_KEY_STORAGE_KEY);
    } else {
        window.localStorage.setItem(CUSTOM_KEY_STORAGE_KEY, key.trim());
    }
}

export function loadTenant(): Tenant {
    if (typeof window === "undefined") return DEFAULT_TENANT;
    const id = window.localStorage.getItem(TENANT_STORAGE_KEY);
    return TENANTS.find((t) => t.id === id) ?? DEFAULT_TENANT;
}

export function saveTenant(t: Tenant): void {
    if (typeof window === "undefined") return;
    window.localStorage.setItem(TENANT_STORAGE_KEY, t.id);
}

export function loadRole(): Role {
    if (typeof window === "undefined") return DEFAULT_ROLE;
    const v = window.localStorage.getItem(ROLE_STORAGE_KEY);
    return (ROLES as readonly string[]).includes(v ?? "") ? (v as Role) : DEFAULT_ROLE;
}

export function saveRole(r: Role): void {
    if (typeof window === "undefined") return;
    window.localStorage.setItem(ROLE_STORAGE_KEY, r);
}
