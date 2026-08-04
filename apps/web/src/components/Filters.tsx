// Filter + sort + search controls. Emits a single onChange with the
// full ListParams so the parent maintains one source of truth and the
// query hook re-keys on any change.

import type { ListParams, SortDir, SortKey } from "../api/inventory.js";
import { useEffect, useState } from "react";

const STATUSES = ["pending", "active", "disconnected"] as const;
const CATEGORIES = ["voice", "data", "wireless", "other"] as const;
const SORT_KEYS: readonly SortKey[] = ["createdAt", "updatedAt", "status", "serviceNumber", "productName"];

interface Props {
    value: ListParams;
    onChange: (next: ListParams) => void;
}

export function Filters({ value, onChange }: Props) {
    const [q, setQ] = useState(value.q ?? "");
    useEffect(() => { setQ(value.q ?? ""); }, [value.q]);

    // 300 ms debounce — the API contract's search minimum is 2 chars; do the
    // debounce here, not in the API layer, so a keystroke pulse doesn't
    // fire a burst of aborted requests.
    useEffect(() => {
        const id = setTimeout(() => {
            const nextQ = q.trim();
            const currentQ = (value.q ?? "").trim();
            if (nextQ !== currentQ) onChange({ ...value, q: nextQ || undefined });
        }, 300);
        return () => clearTimeout(id);
    }, [q, value, onChange]);

    const toggle = (arr: string[] | undefined, v: string): string[] | undefined => {
        const set = new Set(arr ?? []);
        if (set.has(v)) set.delete(v); else set.add(v);
        return set.size === 0 ? undefined : [...set];
    };

    // Show Clear when any filter is set. `sort`/`dir`/`pageSize` are baseline
    // display state, not user-applied filters, so they don't count.
    const hasActiveFilters =
        (value.q?.trim() ?? "") !== ""
        || (value.status?.length ?? 0) > 0
        || (value.productCategory?.length ?? 0) > 0
        || (value.state?.length ?? 0) > 0;

    const clearFilters = () => {
        setQ("");
        onChange({
            ...value,
            q: undefined,
            status: undefined,
            productCategory: undefined,
            state: undefined,
        });
    };

    return (
        <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 bg-white px-4 py-3">
            <input
                type="search"
                placeholder="Search service #, product, address…"
                value={q}
                onChange={(e) => setQ(e.target.value)}
                data-testid="search-input"
                className="w-72 rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
            />

            <FilterGroup
                label="Status"
                options={STATUSES}
                selected={value.status}
                onToggle={(v) => onChange({ ...value, status: toggle(value.status, v) })}
            />
            <FilterGroup
                label="Category"
                options={CATEGORIES}
                selected={value.productCategory}
                onToggle={(v) => onChange({ ...value, productCategory: toggle(value.productCategory, v) })}
            />

            {hasActiveFilters && (
                <button
                    type="button"
                    onClick={clearFilters}
                    data-testid="filter-clear"
                    title="Reset q + status + category + state"
                    className="rounded-md px-2 py-1 text-xs text-slate-600 ring-1 ring-inset ring-slate-300 hover:bg-slate-100"
                >
                    Clear filters
                </button>
            )}

            <div className="flex items-center gap-2 text-xs text-slate-600">
                <span>Sort</span>
                <select
                    value={value.sort ?? "createdAt"}
                    onChange={(e) => onChange({ ...value, sort: e.target.value as SortKey })}
                    className="rounded-md border border-slate-300 bg-white px-2 py-1 text-xs focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                >
                    {SORT_KEYS.map((k) => <option key={k} value={k}>{k}</option>)}
                </select>
                <select
                    value={value.dir ?? "desc"}
                    onChange={(e) => onChange({ ...value, dir: e.target.value as SortDir })}
                    className="rounded-md border border-slate-300 bg-white px-2 py-1 text-xs focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                >
                    <option value="desc">desc</option>
                    <option value="asc">asc</option>
                </select>
            </div>
        </div>
    );
}

function FilterGroup({ label, options, selected, onToggle }: {
    label: string; options: readonly string[]; selected?: string[]; onToggle: (v: string) => void;
}) {
    return (
        <fieldset className="flex items-center gap-1.5 border-0 p-0 m-0">
            <legend className="text-xs text-slate-500 pr-1 float-none inline">{label}</legend>
            {options.map((o) => {
                const on = selected?.includes(o) ?? false;
                return (
                    <button
                        key={o}
                        type="button"
                        onClick={() => onToggle(o)}
                        data-testid={`filter-${label.toLowerCase()}-${o}`}
                        className={`rounded-full px-2.5 py-0.5 text-[11px] font-medium ring-1 ring-inset transition ${
                            on
                                ? "bg-indigo-600 text-white ring-indigo-600"
                                : "bg-white text-slate-600 ring-slate-300 hover:bg-slate-100"
                        }`}
                    >
                        {o}
                    </button>
                );
            })}
        </fieldset>
    );
}
