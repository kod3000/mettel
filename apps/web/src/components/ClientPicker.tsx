import { ROLES, TENANTS, type Role, type Tenant } from "../tenants.js";

interface Props {
    tenant: Tenant;
    role: Role;
    onTenantChange: (t: Tenant) => void;
    onRoleChange: (r: Role) => void;
}

export function ClientPicker({ tenant, role, onTenantChange, onRoleChange }: Props) {
    return (
        <div className="flex items-center gap-3 text-xs text-slate-600">
            <label className="flex items-center gap-2" title="Switch tenant / X-Api-Key">
                <span className="text-slate-500">Client</span>
                <select
                    value={tenant.id}
                    data-testid="client-picker"
                    onChange={(e) => {
                        const next = TENANTS.find((t) => t.id === e.target.value);
                        if (next) onTenantChange(next);
                    }}
                    className="rounded-md border border-slate-300 bg-white px-2 py-1 text-xs font-medium text-slate-700 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                >
                    {TENANTS.map((t) => (
                        <option key={t.id} value={t.id}>{t.label}</option>
                    ))}
                </select>
            </label>
            <label className="flex items-center gap-2" title="Switch role — reader can only view, worker can insert/update, admin can also delete">
                <span className="text-slate-500">Role</span>
                <select
                    value={role}
                    data-testid="role-picker"
                    onChange={(e) => onRoleChange(e.target.value as Role)}
                    className={`rounded-md border px-2 py-1 text-xs font-medium focus:outline-none focus:ring-1 ${
                        role === "reader"
                            ? "border-slate-300 bg-slate-100 text-slate-600 focus:border-slate-500 focus:ring-slate-500"
                            : role === "worker"
                                ? "border-sky-300 bg-sky-50 text-sky-800 focus:border-sky-500 focus:ring-sky-500"
                                : "border-indigo-300 bg-indigo-50 text-indigo-800 focus:border-indigo-500 focus:ring-indigo-500"
                    }`}
                >
                    {ROLES.map((r) => (
                        <option key={r} value={r}>{r}</option>
                    ))}
                </select>
            </label>
        </div>
    );
}
