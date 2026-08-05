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
} from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useEffect, useMemo, useRef } from "react";
import type { InventoryRow, ListParams } from "../api/inventory.js";
import { useInventoryList } from "../hooks/useInventoryList.js";
import { CountDisplay } from "./CountDisplay.js";

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
const COLUMN_WIDTHS: Array<[keyof InventoryRow, string, string]> = [
    ["serviceNumber",   "Service #",  "140px"],
    ["productCategory", "Category",   "110px"],
    ["productName",     "Product",    "minmax(220px, 2fr)"],
    ["status",          "Status",     "120px"],
    ["city",            "City",       "140px"],
    ["state",           "State",      "70px"],
    ["assignee",        "Assignee",   "120px"],
    ["createdAt",       "Created",    "170px"],
    ["updatedAt",       "Updated",    "170px"],
];
const GRID_TEMPLATE = COLUMN_WIDTHS.map(([, , w]) => w).join(" ");
const MIN_TABLE_WIDTH = "1300px";

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

    const sorting: SortingState = useMemo(() => {
        const key = params.sort ?? "createdAt";
        return [{ id: key, desc: (params.dir ?? "desc") === "desc" }];
    }, [params.sort, params.dir]);

    const table = useReactTable({
        data: rows,
        columns,
        state: { sorting },
        getCoreRowModel: getCoreRowModel(),
        manualSorting: true,
        manualPagination: true,
        onSortingChange: (updater) => {
            const next = typeof updater === "function" ? updater(sorting) : updater;
            const first = next[0];
            if (!first || !SORTABLE.has(first.id)) return;
            onParamsChange({ ...params, sort: first.id as ListParams["sort"], dir: first.desc ? "desc" : "asc" });
        },
    });

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
            <CountDisplay
                totalEstimate={lastPage?.totalEstimate}
                filteredCount={lastPage?.filteredCount}
                lastServerMs={lastPage?.tookMs}
                loaded={rows.length} />

            {/* Horizontal + vertical scroll container. Row and header both use
                the same grid-template-columns so alignment stays true even
                as rows are virtualized out of the DOM. */}
            <div
                ref={scrollRef}
                data-testid="grid-scroll"
                className="flex-1 overflow-auto border-t border-slate-200 relative"
            >
                <div style={{ minWidth: MIN_TABLE_WIDTH }}>
                    {/* Sticky header */}
                    <div
                        className="sticky top-0 z-10 bg-slate-50 border-b border-slate-200 text-[11px] font-semibold tracking-wide text-slate-500 uppercase"
                        style={{ display: "grid", gridTemplateColumns: GRID_TEMPLATE }}>
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
                                            gridTemplateColumns: GRID_TEMPLATE,
                                        }}
                                        className={rowClassName(clickable, gpuHover)}
                                    >
                                        {row.getVisibleCells().map((c) => (
                                            <div
                                                key={c.id}
                                                className="px-3 py-2 border-r border-slate-100 last:border-r-0 truncate flex items-center"
                                            >
                                                {flexRender(c.column.columnDef.cell, c.getContext())}
                                            </div>
                                        ))}
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
