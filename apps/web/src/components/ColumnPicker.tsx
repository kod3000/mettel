import { useEffect, useRef, useState } from "react";

// Column visibility popover with drag-to-reorder. Cog button in the grid
// header opens a small panel; checkboxes toggle each column's visibility,
// and each row has a grip (⋮⋮) that acts as a drag handle for reordering.
// Both visibility and order persist to localStorage per browser so the
// operator's preferences travel with them, not the tenant.
//
// Required columns cannot be hidden (protects against a fully-empty grid)
// but they can be reordered.

interface Props {
    // Current display order — caller reorders and passes the new list
    // back through onReorder. Keeps the picker stateless; the parent
    // (grid) is the single source of truth for order + visibility.
    columns: readonly { id: string; label: string; required?: boolean }[];
    visible: Record<string, boolean>;
    onChange: (next: Record<string, boolean>) => void;
    onReorder?: (nextOrder: string[]) => void;
}

export function ColumnPicker({ columns, visible, onChange, onReorder }: Props) {
    const [open, setOpen] = useState(false);
    const [dragIndex, setDragIndex] = useState<number | null>(null);
    const [overIndex, setOverIndex] = useState<number | null>(null);
    const ref = useRef<HTMLDivElement>(null);

    // Click-outside + Esc dismiss.
    useEffect(() => {
        if (!open) return;
        const onDoc = (e: MouseEvent) => {
            if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
        };
        const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
        document.addEventListener("mousedown", onDoc);
        document.addEventListener("keydown", onKey);
        return () => {
            document.removeEventListener("mousedown", onDoc);
            document.removeEventListener("keydown", onKey);
        };
    }, [open]);

    const hiddenCount = columns.filter((c) => visible[c.id] === false).length;

    function handleDrop(toIndex: number) {
        if (dragIndex === null || dragIndex === toIndex) {
            setDragIndex(null); setOverIndex(null); return;
        }
        const ids = columns.map((c) => c.id);
        const moved = ids[dragIndex];
        ids.splice(dragIndex, 1);
        // Removing before the drop target shifts the target index down.
        const insertAt = dragIndex < toIndex ? toIndex - 1 : toIndex;
        ids.splice(insertAt, 0, moved);
        setDragIndex(null); setOverIndex(null);
        onReorder?.(ids);
    }

    return (
        <div ref={ref} className="relative">
            <button
                type="button"
                onClick={() => setOpen((v) => !v)}
                title={hiddenCount > 0 ? `${hiddenCount} column${hiddenCount === 1 ? "" : "s"} hidden` : "Choose visible columns"}
                data-testid="column-picker-toggle"
                aria-haspopup="menu"
                aria-expanded={open}
                className="flex items-center gap-1 rounded-md border border-slate-300 bg-white px-2 py-1 text-xs text-slate-700 hover:bg-slate-50"
            >
                <CogIcon />
                {hiddenCount > 0 && (
                    <span className="rounded-full bg-slate-100 px-1.5 text-[10px] font-medium text-slate-600">
                        {hiddenCount}
                    </span>
                )}
            </button>

            {open && (
                <div
                    role="menu"
                    data-testid="column-picker-menu"
                    className="absolute right-0 top-full z-20 mt-1 w-60 rounded-md border border-slate-200 bg-white p-2 shadow-lg"
                >
                    <div className="flex items-center justify-between px-1 pb-2 border-b border-slate-100">
                        <span className="text-[11px] font-semibold uppercase tracking-wide text-slate-500">
                            Columns
                        </span>
                        <button
                            type="button"
                            onClick={() => {
                                const next: Record<string, boolean> = {};
                                for (const c of columns) next[c.id] = true;
                                onChange(next);
                                // Reset also snaps order back — parent
                                // interprets an empty array as "clear
                                // saved order, use static default".
                                onReorder?.([]);
                            }}
                            className="text-[11px] text-indigo-600 hover:text-indigo-700"
                        >
                            reset
                        </button>
                    </div>
                    <div className="px-1 pt-1 text-[10px] text-slate-400">
                        Drag <span className="text-slate-500">⋮⋮</span> to reorder
                    </div>
                    <ul className="mt-1 space-y-0.5">
                        {columns.map((c, i) => {
                            const on = visible[c.id] !== false;
                            const locked = c.required === true;
                            const isDragOver = overIndex === i && dragIndex !== null && dragIndex !== i;
                            const isDragging = dragIndex === i;
                            return (
                                <li
                                    key={c.id}
                                    draggable
                                    onDragStart={() => setDragIndex(i)}
                                    onDragOver={(e) => {
                                        e.preventDefault();
                                        if (dragIndex !== null && dragIndex !== i && overIndex !== i) setOverIndex(i);
                                    }}
                                    onDragLeave={() => { if (overIndex === i) setOverIndex(null); }}
                                    onDrop={(e) => { e.preventDefault(); handleDrop(i); }}
                                    onDragEnd={() => { setDragIndex(null); setOverIndex(null); }}
                                    className={[
                                        "flex items-center gap-1 rounded border transition-colors",
                                        isDragOver ? "border-indigo-300 bg-indigo-50 shadow-[inset_0_2px_0_theme(colors.indigo.600)]" : "border-transparent",
                                        isDragging ? "opacity-40" : "",
                                    ].join(" ")}
                                >
                                    <span
                                        aria-hidden="true"
                                        title="Drag to reorder"
                                        className="cursor-grab select-none px-1 text-slate-400 text-xs active:cursor-grabbing"
                                    >⋮⋮</span>
                                    <label
                                        className={`flex flex-1 items-center gap-2 rounded px-1.5 py-1 text-xs ${
                                            locked ? "cursor-not-allowed text-slate-400" : "cursor-pointer text-slate-700 hover:bg-slate-50"
                                        }`}
                                        title={locked ? "Required column" : undefined}
                                    >
                                        <input
                                            type="checkbox"
                                            checked={on}
                                            disabled={locked}
                                            data-testid={`column-picker-${c.id}`}
                                            onChange={(e) => onChange({ ...visible, [c.id]: e.target.checked })}
                                            className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500 disabled:opacity-40"
                                        />
                                        <span>{c.label}</span>
                                    </label>
                                </li>
                            );
                        })}
                    </ul>
                </div>
            )}
        </div>
    );
}

function CogIcon() {
    return (
        <svg viewBox="0 0 20 20" fill="currentColor" className="h-3.5 w-3.5" aria-hidden="true">
            <path fillRule="evenodd" d="M11.49 3.17c-.38-1.56-2.6-1.56-2.98 0a1.53 1.53 0 0 1-2.29.95c-1.37-.83-2.94.74-2.11 2.11a1.53 1.53 0 0 1-.95 2.29c-1.56.38-1.56 2.6 0 2.98a1.53 1.53 0 0 1 .95 2.29c-.83 1.37.74 2.94 2.11 2.11a1.53 1.53 0 0 1 2.29.95c.38 1.56 2.6 1.56 2.98 0a1.53 1.53 0 0 1 2.29-.95c1.37.83 2.94-.74 2.11-2.11a1.53 1.53 0 0 1 .95-2.29c1.56-.38 1.56-2.6 0-2.98a1.53 1.53 0 0 1-.95-2.29c.83-1.37-.74-2.94-2.11-2.11a1.53 1.53 0 0 1-2.29-.95zM10 13a3 3 0 1 0 0-6 3 3 0 0 0 0 6z" clipRule="evenodd" />
        </svg>
    );
}

// localStorage load/save helpers exported for use by the grid.
const VISIBILITY_KEY = "bruin.columnVisibility";
const ORDER_KEY = "bruin.columnOrder";

export function loadColumnVisibility(): Record<string, boolean> {
    if (typeof window === "undefined") return {};
    try {
        const raw = window.localStorage.getItem(VISIBILITY_KEY);
        return raw ? (JSON.parse(raw) as Record<string, boolean>) : {};
    } catch { return {}; }
}

export function saveColumnVisibility(v: Record<string, boolean>): void {
    if (typeof window === "undefined") return;
    window.localStorage.setItem(VISIBILITY_KEY, JSON.stringify(v));
}

export function loadColumnOrder(): string[] {
    if (typeof window === "undefined") return [];
    try {
        const raw = window.localStorage.getItem(ORDER_KEY);
        const arr = raw ? (JSON.parse(raw) as unknown) : null;
        return Array.isArray(arr) && arr.every((s) => typeof s === "string") ? (arr as string[]) : [];
    } catch { return []; }
}

export function saveColumnOrder(order: string[]): void {
    if (typeof window === "undefined") return;
    if (order.length === 0) window.localStorage.removeItem(ORDER_KEY);
    else window.localStorage.setItem(ORDER_KEY, JSON.stringify(order));
}
