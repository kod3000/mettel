import { TENANTS, type Tenant } from "../tenants.js";

interface Props {
    value: Tenant;
    onChange: (t: Tenant) => void;
}

export function ClientPicker({ value, onChange }: Props) {
    return (
        <label className="flex items-center gap-2 text-xs text-slate-600" title="Switch tenant / X-Api-Key">
            <span className="text-slate-500">Client</span>
            <select
                value={value.id}
                data-testid="client-picker"
                onChange={(e) => {
                    const next = TENANTS.find((t) => t.id === e.target.value);
                    if (next) onChange(next);
                }}
                className="rounded-md border border-slate-300 bg-white px-2 py-1 text-xs font-medium text-slate-700 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
            >
                {TENANTS.map((t) => (
                    <option key={t.id} value={t.id}>{t.label}</option>
                ))}
            </select>
        </label>
    );
}
