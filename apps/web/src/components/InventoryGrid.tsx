// Virtualized grid over TanStack Table + Virtual + Query. Renders the columns
// the contract calls out (Phase 8): serviceNumber, productCategory,
// productName, status, city, state, assignee, createdAt, updatedAt.
//
// Layout: CSS Grid with explicit min column widths so cells never squish to
// unreadable widths. Container scrolls horizontally when the viewport
// is narrower than the sum of columns.

import {
    createColumnHelper,
    flexRender,
    getCoreRowModel,
    useReactTable,
    type SortingState,
    type VisibilityState,
} from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useEffect, useMemo, useRef, useState } from "react";
import type { InventoryRow, ListParams } from "../api/inventory.js";
import { useInventoryList } from "../hooks/useInventoryList.js";
import { CountDisplay } from "./CountDisplay.js";
import { ColumnPicker, loadColumnVisibility, saveColumnVisibility } from "./ColumnPicker.js";

interface Props {
    params: ListParams;
    onParamsChange: (next: ListParams) => void;
    onRowSelect?: (row: InventoryRow) => void;
    /** When true, rows use GPU-accelerated transform hover (bulge + settle). */
    gpuHover?: boolean;
}

const columnHelper = createColumnHelper<InventoryRow>();

// [id, header, min-column-width]. Row + header both use these widths in a
// grid-template-columns definition so headers always align with cells.
// [id, header, min-column-width, required]
// `required` means the ColumnPicker can't hide it — the grid still needs
// SOMETHING to point to when the user clicks a row.
const COLUMN_META: Array<{ id: keyof InventoryRow; label: string; width: string; required?: boolean }> = [
    { id: "serviceNumber",   label: "Service #",  width: "140px" },
    { id: "productCategory", label: "Category",   width: "110px" },
    { id: "productName",     label: "Product",    width: "minmax(220px, 2fr)", required: true },
    { id: "status",          label: "Status",     width: "120px" },
    { id: "city",            label: "City",       width: "140px" },
    { id: "state",           label: "State",      width: "70px" },
    { id: "address",         label: "Address",    width: "minmax(200px, 1.5fr)" },
    { id: "assignee",        label: "Assignee",   width: "120px" },
    { id: "createdAt",       label: "Created",    width: "170px" },
    { id: "updatedAt",       label: "Updated",    width: "170px" },
];

// Wire names of the fields the server searches when no `fields=` param is
// sent — mirrors Filters.SEARCH_FIELDS. Used to compute "matched-in-hidden"
// hints on rows: if the search term appears in a searchable field that
// happens to be currently hidden in the grid, we surface it so the
// operator understands why the row is here.
const DEFAULT_SEARCH_FIELDS: readonly (keyof InventoryRow)[] = [
    "productName", "serviceNumber", "city", "state", "address", "assignee", "notes",
];
const PICKER_COLUMNS = COLUMN_META.map((c) => ({ id: c.id as string, label: c.label, required: c.required }));

const columns = [
    columnHelper.accessor("serviceNumber", { header: "Service #", cell: (i) => (
        <span className="font-mono text-[12.5px] tracking-tight">{i.getValue() ?? ""}</span>
    )}),
    columnHelper.accessor("productCategory", { header: "Category", cell: (i) => (
        <span className="text-[12px] text-slate-600 uppercase tracking-wide">{i.getValue()}</span>
    )}),
    columnHelper.accessor("productName", { header: "Product" }),
    columnHelper.accessor("status", { header: "Status", cell: (i) => <StatusBadge s={i.getValue() ?? ""} /> }),
    columnHelper.accessor("city", { header: "City" }),
    columnHelper.accessor("state", { header: "State", cell: (i) => (
        <span className="font-mono text-[12.5px]">{i.getValue() ?? ""}</span>
    )}),
    columnHelper.accessor("address", { header: "Address", cell: (i) => (
        <span className="text-[12.5px] text-slate-700 truncate" title={i.getValue() ?? undefined}>
            {i.getValue() ?? "—"}
        </span>
    )}),
    columnHelper.accessor("assignee", { header: "Assignee", cell: (i) => (
        <span className="font-mono text-[12.5px] text-slate-600">{i.getValue() ?? "—"}</span>
    )}),
    columnHelper.accessor("createdAt", { header: "Created", cell: (i) => (
        <span className="tabular-nums text-[12.5px] text-slate-600">{fmtDate(i.getValue())}</span>
    )}),
    columnHelper.accessor("updatedAt", { header: "Updated", cell: (i) => (
        <span className="tabular-nums text-[12.5px] text-slate-600">{fmtDate(i.getValue())}</span>
    )}),
];

// Only server-sort the five columns the API supports.
const SORTABLE = new Set(["serviceNumber", "productName", "status", "createdAt", "updatedAt"]);

export function InventoryGrid({ params, onParamsChange, onRowSelect, gpuHover = false }: Props) {
    const query = useInventoryList(params);
    const rows: InventoryRow[] = useMemo(
        () => query.data?.pages.flatMap((p) => p.rows ?? []) ?? [],
        [query.data]);

    // Column visibility — persisted per browser (not per tenant since column
    // preference tracks the operator, not the data set).
    const [visibility, setVisibility] = useState<VisibilityState>(() => loadColumnVisibility());
    const applyVisibility = (next: VisibilityState) => {
        // Force required columns back on if the caller cleared everything
        // via localStorage tampering — belt for the picker's disabled state.
        for (const c of COLUMN_META) if (c.required) next[c.id] = true;
        saveColumnVisibility(next);
        setVisibility(next);
    };

    // Derived grid-template-columns from the visible subset. Header + rows
    // both reference this so alignment stays true when columns hide/show.
    const gridTemplate = useMemo(() =>
        COLUMN_META.filter((c) => visibility[c.id] !== false).map((c) => c.width).join(" "),
        [visibility]);
    const minTableWidth = useMemo(() => {
        // Sum of numeric column widths (px), rough enough for horizontal
        // scroll thresholds. Fractional/minmax columns count as their
        // minimum. Prevents the layout from collapsing on very narrow
        // viewports when few columns are visible.
        let total = 0;
        for (const c of COLUMN_META) {
            if (visibility[c.id] === false) continue;
            const m = /(\d+)px/.exec(c.width);
            total += m ? Number(m[1]) : 200;
        }
        return `${Math.max(total, 400)}px`;
    }, [visibility]);

    const sorting: SortingState = useMemo(() => {
        const key = params.sort ?? "createdAt";
        return [{ id: key, desc: (params.dir ?? "desc") === "desc" }];
    }, [params.sort, params.dir]);

    const table = useReactTable({
        data: rows,
        columns,
        state: { sorting, columnVisibility: visibility },
        getCoreRowModel: getCoreRowModel(),
        manualSorting: true,
        manualPagination: true,
        onColumnVisibilityChange: (updater) => {
            const next = typeof updater === "function" ? updater(visibility) : updater;
            applyVisibility(next);
        },
        onSortingChange: (updater) => {
            const next = typeof updater === "function" ? updater(sorting) : updater;
            const first = next[0];
            if (!first || !SORTABLE.has(first.id)) return;
            onParamsChange({ ...params, sort: first.id as ListParams["sort"], dir: first.desc ? "desc" : "asc" });
        },
    });

    // ---- Hidden-field match hints -----------------------------------------
    // When the user's search term matches a row via a field that's currently
    // hidden from the grid, surface a small chip on the row so they can see
    // "why is this row here?". Client-side only — no server change needed
    // because we already have the row data + the query string.
    const queryTokens = useMemo(() => {
        const q = (params.q ?? "").trim().toLowerCase();
        if (q.length < 2) return [];
        return q.split(/\s+/).filter((t) => t.length > 0);
    }, [params.q]);

    // Effective set of columns the SERVER searched: fields= if set,
    // otherwise the default whole-tsvector list.
    const searchedFields: readonly (keyof InventoryRow)[] = useMemo(
        () => (params.fields && params.fields.length > 0
            ? params.fields as (keyof InventoryRow)[]
            : DEFAULT_SEARCH_FIELDS),
        [params.fields]);

    // …of those, the ones NOT currently visible in the grid. Notes is
    // always here because there's no Notes column.
    const hiddenSearched = useMemo(
        () => searchedFields.filter((f) => visibility[f as string] === false ||
            !COLUMN_META.some((c) => c.id === f)),
        [searchedFields, visibility]);

    // Per-row: which hidden searched fields actually contain the query.
    // Empty array = no chip. Called per virtualized row so it needs to be
    // O(fields×tokens) not O(all rows).
    const hiddenMatchesFor = (row: InventoryRow): string[] => {
        if (queryTokens.length === 0 || hiddenSearched.length === 0) return [];
        const hits: string[] = [];
        for (const f of hiddenSearched) {
            const raw = row[f];
            if (typeof raw !== "string" || raw.length === 0) continue;
            const lower = raw.toLowerCase();
            if (queryTokens.some((t) => lower.includes(t))) hits.push(f as string);
        }
        return hits;
    };

    const scrollRef = useRef<HTMLDivElement>(null);
    const virtualizer = useVirtualizer({
        count: rows.length,
        estimateSize: () => 40,
        getScrollElement: () => scrollRef.current,
        overscan: 12,
    });

    useEffect(() => {
        const items = virtualizer.getVirtualItems();
        const lastRendered = items[items.length - 1];
        if (!lastRendered) return;
        if (lastRendered.index >= rows.length - 8 && query.hasNextPage && !query.isFetchingNextPage) {
            query.fetchNextPage();
        }
    }, [virtualizer.getVirtualItems(), rows.length, query]);

    const lastPage = query.data?.pages[query.data.pages.length - 1];

    return (
        <div className="flex flex-col h-full min-h-[480px] bg-white">
            <div className="flex items-center gap-3 border-b border-slate-100 px-4 py-1.5">
                <div className="flex-1">
                    <CountDisplay
                        totalEstimate={lastPage?.totalEstimate}
                        filteredCount={lastPage?.filteredCount}
                        lastServerMs={lastPage?.tookMs}
                        loaded={rows.length} />
                </div>
                <ColumnPicker
                    columns={PICKER_COLUMNS}
                    visible={visibility}
                    onChange={applyVisibility}
                />
            </div>

            {/* Horizontal + vertical scroll container. Row and header both use
                the same grid-template-columns so alignment stays true even
                as rows are virtualized out of the DOM. */}
            <div
                ref={scrollRef}
                data-testid="grid-scroll"
                className="flex-1 overflow-auto border-t border-slate-200 relative"
            >
                <div style={{ minWidth: minTableWidth }}>
                    {/* Sticky header */}
                    <div
                        className="sticky top-0 z-10 bg-slate-50 border-b border-slate-200 text-[11px] font-semibold tracking-wide text-slate-500 uppercase"
                        style={{ display: "grid", gridTemplateColumns: gridTemplate }}>
                        {table.getHeaderGroups()[0].headers.map((h) => {
                            const isSortable = SORTABLE.has(h.column.id);
                            return (
                                <div
                                    key={h.id}
                                    data-testid={`th-${h.column.id}`}
                                    onClick={isSortable ? h.column.getToggleSortingHandler() : undefined}
                                    className={`px-3 py-2.5 border-r border-slate-200 last:border-r-0 whitespace-nowrap select-none ${
                                        isSortable ? "cursor-pointer hover:bg-slate-100" : ""
                                    }`}>
                                    {flexRender(h.column.columnDef.header, h.getContext())}
                                    {isSortable ? sortIndicator(h.column.getIsSorted()) : null}
                                </div>
                            );
                        })}
                    </div>

                    {/* Body — pending, error, empty, or virtualized rows */}
                    {query.isPending ? (
                        <div className="px-4 py-6 text-sm text-slate-500">Loading…</div>
                    ) : query.isError ? (
                        <div className="px-4 py-6 text-sm text-red-700 bg-red-50">
                            {query.error.message}
                        </div>
                    ) : rows.length === 0 ? (
                        <div className="px-4 py-6 text-sm text-slate-500">
                            No inventory matches these filters.
                        </div>
                    ) : (
                        <div style={{ position: "relative", height: virtualizer.getTotalSize() }}>
                            {virtualizer.getVirtualItems().map((vr) => {
                                const row = table.getRowModel().rows[vr.index];
                                if (!row) return null;
                                const rowData = row.original;
                                const clickable = Boolean(onRowSelect);
                                const hiddenHits = hiddenMatchesFor(rowData);
                                return (
                                    <div
                                        key={row.id}
                                        data-testid="grid-row"
                                        role={clickable ? "button" : undefined}
                                        tabIndex={clickable ? 0 : undefined}
                                        onClick={clickable ? () => onRowSelect!(rowData) : undefined}
                                        onKeyDown={clickable ? (e) => {
                                            if (e.key === "Enter" || e.key === " ") {
                                                e.preventDefault();
                                                onRowSelect!(rowData);
                                            }
                                        } : undefined}
                                        style={{
                                            position: "absolute",
                                            top: vr.start,
                                            left: 0, right: 0,
                                            height: vr.size,
                                            display: "grid",
                                            gridTemplateColumns: gridTemplate,
                                        }}
                                        className={rowClassName(clickable, gpuHover) + " relative"}
                                    >
                                        {row.getVisibleCells().map((c) => (
                                            <div
                                                key={c.id}
                                                className="px-3 py-2 border-r border-slate-100 last:border-r-0 truncate flex items-center"
                                            >
                                                {flexRender(c.column.columnDef.cell, c.getContext())}
                                            </div>
                                        ))}
                                        {hiddenHits.length > 0 && (
                                            <HiddenMatchChip fields={hiddenHits} row={rowData} tokens={queryTokens} />
                                        )}
                                    </div>
                                );
                            })}
                        </div>
                    )}
                    {query.isFetchingNextPage && (
                        <div className="px-4 py-3 text-xs text-slate-500">Loading more…</div>
                    )}
                </div>
            </div>
        </div>
    );
}

// Small pill anchored at the right edge of a row that had a search hit in
// a column the operator has hidden. Hover reveals the offending snippet so
// they can decide whether to bring the column back or ignore. Interactive
// only via title tooltip — no click handler because the row already
// consumes click for opening the drawer.
function HiddenMatchChip({
    fields, row, tokens,
}: {
    fields: string[];
    row: InventoryRow;
    tokens: string[];
}) {
    // Build a compact tooltip: "notes: '…snippet…' · address: '…snippet…'"
    const tip = fields.map((f) => {
        const raw = (row[f as keyof InventoryRow] as string | null | undefined) ?? "";
        const lower = raw.toLowerCase();
        const idx = tokens
            .map((t) => lower.indexOf(t))
            .filter((i) => i >= 0)
            .sort((a, b) => a - b)[0] ?? 0;
        const start = Math.max(0, idx - 15);
        const end = Math.min(raw.length, idx + 40);
        const snippet = (start > 0 ? "…" : "") + raw.slice(start, end) + (end < raw.length ? "…" : "");
        return `${f}: "${snippet}"`;
    }).join(" · ");

    return (
        <span
            title={tip}
            data-testid="hidden-match-chip"
            className="pointer-events-auto absolute right-2 top-1/2 -translate-y-1/2 inline-flex items-center gap-1 rounded-full bg-amber-50 px-1.5 py-0.5 text-[10px] font-medium text-amber-800 ring-1 ring-amber-200 shadow-sm"
            onClick={(e) => e.stopPropagation()}
        >
            matched: {fields.join(", ")}
        </span>
    );
}

function StatusBadge({ s }: { s: string }) {
    const styles: Record<string, string> = {
        pending:      "bg-amber-100 text-amber-800 ring-amber-200",
        active:       "bg-emerald-100 text-emerald-800 ring-emerald-200",
        disconnected: "bg-rose-100 text-rose-800 ring-rose-200",
    };
    const cls = styles[s] ?? "bg-slate-100 text-slate-700 ring-slate-200";
    return (
        <span className={`inline-flex items-center rounded-full ring-1 ring-inset px-2 py-0.5 text-[11px] font-semibold ${cls}`}>
            {s}
        </span>
    );
}

function fmtDate(v: string | null | undefined): string {
    if (!v) return "";
    try {
        const d = new Date(v);
        return d.toLocaleString(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
    } catch { return v; }
}

// GPU-hover mode enables a subtle scale-y bulge on hover using a springy
// timing curve. The overshoot easing (cubic-bezier) makes hover-in overshoot
// slightly then settle, and hover-out briefly dip below the resting height
// before returning — the "slight bulge, slight dip" effect. Rendered via
// utility classes defined in index.css so we can share the timing constants.
function rowClassName(clickable: boolean, gpu: boolean): string {
    const base = "border-b border-slate-100 text-[13px] text-slate-800 hover:bg-slate-50";
    const click = clickable ? "cursor-pointer" : "";
    // `origin-center` keeps the transform centered so the row doesn't
    // "walk" as it scales; `will-change` lifts it to a GPU layer.
    const gpuClasses = gpu ? "row-gpu origin-center" : "";
    return [base, click, gpuClasses].filter(Boolean).join(" ");
}

function sortIndicator(dir: false | "asc" | "desc"): string {
    if (dir === "asc") return " ↑";
    if (dir === "desc") return " ↓";
    return "";
}
