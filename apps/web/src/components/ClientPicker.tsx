import { useState } from "react";
import { ROLES, TENANTS, type Role, type Tenant } from "../tenants.js";

interface Props {
    tenant: Tenant;
    role: Role;
    onTenantChange: (t: Tenant) => void;
    onRoleChange: (r: Role) => void;
    // When set, the built-in tenant/role picker is bypassed and this
    // raw string goes out as X-Api-Key. Populated by pasting a key
    // provisioned at auth.mettel.exercise.dany.codes (the server's
    // IdentityFallback resolver knows how to accept it).
    customKey: string | null;
    onCustomKeyChange: (key: string | null) => void;
    // What /me returned for the current key. Only rendered when the
    // custom key is active so the operator can see which tenant + role
    // the pasted key actually resolved to on the server.
    resolved?: { clientName: string; role: Role } | null;
}

export function ClientPicker({
    tenant, role, onTenantChange, onRoleChange,
    customKey, onCustomKeyChange, resolved,
}: Props) {
    const [inputOpen, setInputOpen] = useState(false);
    const [draft, setDraft] = useState("");

    // Custom-key mode: the built-in Client + Role selects don't apply,
    // so we hide them and show a chip with a clear button instead.
    // Once /me resolves we also render "<tenant> · <role>" so the
    // operator can confirm the pasted key mapped to who they expected.
    if (customKey) {
        const roleClass = resolved
            ? (resolved.role === "reader"
                ? "border-slate-300 bg-slate-100 text-slate-600"
                : resolved.role === "worker"
                    ? "border-sky-300 bg-sky-50 text-sky-800"
                    : "border-indigo-300 bg-indigo-50 text-indigo-800")
            : "border-slate-300 bg-slate-100 text-slate-500";
        return (
            <div className="flex items-center gap-2 text-xs text-slate-600" data-testid="custom-key-chip">
                <span className="text-slate-500">Key</span>
                <span
                    className="inline-flex items-center gap-2 rounded-md border border-emerald-300 bg-emerald-50 px-2 py-1 font-mono text-xs text-emerald-800"
                    title={customKey}
                >
                    custom: {customKey.slice(0, 12)}…
                    <button
                        type="button"
                        onClick={() => onCustomKeyChange(null)}
                        className="rounded p-0.5 text-emerald-700 hover:bg-emerald-100"
                        title="Clear custom key and return to the demo picker"
                        aria-label="Clear custom key"
                    >
                        ✕
                    </button>
                </span>
                {resolved && (
                    <span
                        className="inline-flex items-center gap-2 text-slate-600"
                        data-testid="custom-key-resolved"
                    >
                        <span className="text-slate-500">as</span>
                        <span className="font-medium text-slate-800">
                            {resolved.clientName || "unnamed tenant"}
                        </span>
                        <span
                            className={`rounded-md border px-1.5 py-0.5 text-[11px] font-medium ${roleClass}`}
                        >
                            {resolved.role}
                        </span>
                    </span>
                )}
            </div>
        );
    }

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

            {/* Inline "use my own key" affordance — dev-tools-flavour so it
                doesn't compete with the primary Client/Role selects but is
                still discoverable to anyone provisioning a real key. */}
            {inputOpen ? (
                <form
                    className="flex items-center gap-1"
                    onSubmit={(e) => {
                        e.preventDefault();
                        const v = draft.trim();
                        if (v.length > 0) {
                            onCustomKeyChange(v);
                            setDraft("");
                            setInputOpen(false);
                        }
                    }}
                >
                    <input
                        type="text"
                        autoFocus
                        value={draft}
                        onChange={(e) => setDraft(e.target.value)}
                        onKeyDown={(e) => { if (e.key === "Escape") setInputOpen(false); }}
                        placeholder="Paste your X-Api-Key"
                        data-testid="custom-key-input"
                        className="w-64 rounded-md border border-slate-300 bg-white px-2 py-1 font-mono text-xs text-slate-800 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                    />
                    <button
                        type="submit"
                        disabled={draft.trim().length === 0}
                        className="rounded-md border border-indigo-300 bg-indigo-600 px-2 py-1 text-xs font-medium text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
                    >
                        Use
                    </button>
                    <button
                        type="button"
                        onClick={() => { setDraft(""); setInputOpen(false); }}
                        className="rounded-md border border-slate-300 bg-white px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
                    >
                        Cancel
                    </button>
                </form>
            ) : (
                <button
                    type="button"
                    onClick={() => setInputOpen(true)}
                    data-testid="btn-custom-key"
                    className="rounded-md border border-slate-300 bg-white px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
                    title="Paste an X-Api-Key issued at auth.mettel.exercise.dany.codes"
                >
                    Use my own key
                </button>
            )}
        </div>
    );
}
