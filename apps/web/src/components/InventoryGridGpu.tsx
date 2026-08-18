// PixiJS prototype of the inventory grid body. Same props/behavior as
// InventoryGrid but rows are drawn to a single <canvas> via WebGL/WebGPU
// (PixiJS v8 auto-picks; WebGPU on Chrome/Safari, WebGL2 elsewhere).
//
// What's on the GPU: row rectangles, hover highlight, status pills, and
// cell text (PixiJS `Text` rasterizes on CPU then uploads to a GPU
// texture — visually pixel-identical to native canvas text but batched
// alongside our quads).
//
// What stays DOM: sticky header row (keeps click-to-sort accessibility),
// count display, error / empty / loading states. Only the scrollable body
// is canvas.
//
// Virtualization: we pool one PIXI.Container per potentially-visible row
// slot (viewport height / row height + overscan). On scroll we reassign
// which row_index each slot maps to and mutate the child text/graphics
// rather than tearing down + rebuilding. That keeps allocations near zero
// in the scroll path — the whole point of the GPU mode.
//
// Accessibility caveat: canvas is invisible to screen readers. The DOM
// fallback (gpuHover=false) remains the accessible path. A hidden
// aria-live region announces the currently hovered row so at least the
// keyboard/screen-reader flow degrades gracefully.

import { useEffect, useMemo, useRef, useState } from "react";
import { Application, Container, Graphics, Text, TextStyle } from "pixi.js";
import type { InventoryRow, ListParams } from "../api/inventory.js";
import { useInventoryList } from "../hooks/useInventoryList.js";
import { CountDisplay } from "./CountDisplay.js";
import { EmptyState } from "./EmptyState.js";
import type { Role } from "../tenants.js";

interface Props {
    params: ListParams;
    onParamsChange: (next: ListParams) => void;
    onRowSelect?: (row: InventoryRow) => void;
    // Role of the current key, from /me. Feeds the empty-state variant.
    role: Role;
    // Opens the "+ New" modal from inside the empty state.
    onCreateNew?: () => void;
}

// Column layout: [key, header, baseWidth, flexible?]. Mirrors the DOM
// grid's minmax(220px, 2fr) trick for productName — every column is a
// fixed base pixel width, and `productName` (the only flex column)
// absorbs any viewport width beyond the sum of base widths. That gives
// canvas the same "fill the window" behaviour CSS grid provides for free.
const COLS_BASE: Array<[keyof InventoryRow, string, number, boolean]> = [
    ["serviceNumber",   "Service #",  140, false],
    ["productCategory", "Category",   110, false],
    ["productName",     "Product",    220, true],
    ["status",          "Status",     120, false],
    ["city",            "City",       140, false],
    ["state",           "State",      70,  false],
    ["assignee",        "Assignee",   120, false],
    ["createdAt",       "Created",    170, false],
    ["updatedAt",       "Updated",    170, false],
];
const MIN_TABLE_WIDTH = COLS_BASE.reduce((s, c) => s + c[2], 0);

interface ColLayout { widths: number[]; xs: number[]; total: number }
function computeColLayout(available: number): ColLayout {
    const total = Math.max(available, MIN_TABLE_WIDTH);
    const extra = total - MIN_TABLE_WIDTH;
    const widths = COLS_BASE.map(([, , base, flex]) => flex ? base + extra : base);
    const xs: number[] = [0];
    for (let i = 0; i < widths.length - 1; i++) xs.push(xs[i] + widths[i]);
    return { widths, xs, total };
}
const ROW_H = 36;
const OVERSCAN = 4;
const SORTABLE = new Set(["serviceNumber", "productName", "status", "createdAt", "updatedAt"]);

const STATUS_COLORS: Record<string, { bg: number; fg: number }> = {
    pending:      { bg: 0xfef3c7, fg: 0x92400e },
    active:       { bg: 0xd1fae5, fg: 0x065f46 },
    disconnected: { bg: 0xffe4e6, fg: 0x9f1239 },
};
const HOVER_BG = 0xeef2ff;
const ROW_STRIPE = 0xffffff;
const BORDER = 0xf1f5f9;

// One PIXI row-slot: bg rect, per-column Text nodes, and a status pill.
// Reused across many logical rows via `updateRow(idx)`.
interface RowSlot {
    container: Container;
    bg: Graphics;
    pillBg: Graphics;
    pillText: Text;
    texts: Text[]; // one per column (except status which uses the pill)
    rowIndex: number; // which logical row this slot currently displays; -1 = unused
}

export function InventoryGridGpu({ params, onParamsChange, onRowSelect, role, onCreateNew }: Props) {
    const query = useInventoryList(params);
    const rows: InventoryRow[] = useMemo(
        () => query.data?.pages.flatMap((p) => p.rows ?? []) ?? [],
        [query.data]);

    // Hosts the PIXI canvas. We track viewport size via ResizeObserver
    // because PIXI's `resizeTo` polls window, which misses flex-layout
    // resizes inside our column.
    const hostRef = useRef<HTMLDivElement>(null);
    const appRef = useRef<Application | null>(null);
    const rowLayerRef = useRef<Container | null>(null);
    const slotsRef = useRef<RowSlot[]>([]);
    const scrollTopRef = useRef(0);
    const viewportHRef = useRef(0);
    const hoveredIdxRef = useRef(-1);
    const rowsRef = useRef<InventoryRow[]>(rows);
    rowsRef.current = rows;

    // Viewport width drives responsive column widths. State (not ref)
    // because the DOM header row also needs to re-render when it changes.
    const [viewportW, setViewportW] = useState(MIN_TABLE_WIDTH);
    const colLayout = useMemo(() => computeColLayout(viewportW), [viewportW]);
    const colLayoutRef = useRef(colLayout);
    colLayoutRef.current = colLayout;

    const [hoverAnnouncement, setHoverAnnouncement] = useState("");

    // Init PIXI app once per mount. v8 uses async init; we set up the
    // stage and layers here and let subsequent effects drive updates.
    useEffect(() => {
        const host = hostRef.current;
        if (!host) return;
        let disposed = false;
        const app = new Application();

        (async () => {
            await app.init({
                background: 0xffffff,
                antialias: true,
                resolution: window.devicePixelRatio || 1,
                autoDensity: true,
                resizeTo: host,
                // Prefer WebGPU where available; PIXI falls back to WebGL2.
                preference: "webgpu",
            });
            if (disposed) { app.destroy(true); return; }
            host.appendChild(app.canvas);
            appRef.current = app;

            const rowLayer = new Container();
            rowLayer.label = "rows";
            app.stage.addChild(rowLayer);
            rowLayerRef.current = rowLayer;

            viewportHRef.current = host.clientHeight;
            setViewportW(host.clientWidth || MIN_TABLE_WIDTH);
            ensureSlots();
            applyLayoutToSlots();
            layout();
        })();

        // Track host size — height drives the slot pool depth, width drives
        // the responsive column layout (via setViewportW → colLayout state).
        // React dedupes same-value setViewportW so a resize that only changes
        // height doesn't churn the header re-render.
        const ro = new ResizeObserver(() => {
            if (!appRef.current) return;
            const h = host.clientHeight;
            const w = host.clientWidth;
            const heightChanged = h !== viewportHRef.current;
            if (heightChanged) { viewportHRef.current = h; }
            setViewportW(w || MIN_TABLE_WIDTH);
            if (heightChanged) { ensureSlots(); layout(); }
        });
        ro.observe(host);

        return () => {
            disposed = true;
            ro.disconnect();
            if (appRef.current) {
                appRef.current.destroy(true, { children: true, texture: true });
                appRef.current = null;
            }
            slotsRef.current = [];
            rowLayerRef.current = null;
        };
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    // Re-layout whenever the row set changes (new page, filter change, etc.)
    useEffect(() => { layout(); /* eslint-disable-next-line */ }, [rows]);

    // Reposition column-anchored elements when the responsive width changes.
    // Repaint every visible row so the row-bg width + pill x follow suit.
    //
    // `layout()` normally skips slots whose logical rowIndex hasn't changed
    // — that's the fast scroll path. But when colLayout changes we need
    // paintRow to re-run (the pill x is only read inside paintRow, not in
    // paintBg). Invalidating slot.rowIndex forces the full repaint. Without
    // this, toggling the GPU grid off and back on left the status pill at
    // its previous-width x offset until a page refresh.
    useEffect(() => {
        if (!appRef.current) return;
        applyLayoutToSlots();
        for (const s of slotsRef.current) s.rowIndex = -1;
        layout();
        // eslint-disable-next-line
    }, [colLayout]);

    // Grow the pool if the viewport got taller. Never shrink — extra slots
    // just sit invisible, and re-allocating text nodes is expensive.
    function ensureSlots() {
        const app = appRef.current;
        const layer = rowLayerRef.current;
        if (!app || !layer) return;
        const needed = Math.ceil(viewportHRef.current / ROW_H) + OVERSCAN * 2;
        const style = new TextStyle({
            fontFamily: "system-ui, -apple-system, 'SF Pro Text', Segoe UI, Roboto, sans-serif",
            fontSize: 13,
            fill: 0x1f2937,
        });
        const monoStyle = new TextStyle({
            fontFamily: "ui-monospace, 'SF Mono', Menlo, monospace",
            fontSize: 12.5,
            fill: 0x334155,
        });
        while (slotsRef.current.length < needed) {
            const c = new Container();
            const bg = new Graphics();
            c.addChild(bg);
            const texts: Text[] = COLS_BASE.map(([key], i) => {
                const t = new Text({
                    text: "",
                    style: key === "serviceNumber" || key === "state" || key === "assignee" ? monoStyle : style,
                });
                // x is set by applyLayoutToSlots — the column layout can
                // change after slots are created (responsive resize).
                t.x = colLayoutRef.current.xs[i] + 10;
                t.y = (ROW_H - 16) / 2;
                c.addChild(t);
                return t;
            });
            // Status pill lives on top of the status column instead of its text.
            const pillBg = new Graphics();
            const pillText = new Text({
                text: "",
                style: new TextStyle({
                    fontFamily: "system-ui, sans-serif",
                    fontSize: 11,
                    fontWeight: "600",
                    fill: 0x000000,
                }),
            });
            c.addChild(pillBg);
            c.addChild(pillText);

            const slot: RowSlot = { container: c, bg, pillBg, pillText, texts, rowIndex: -1 };
            slotsRef.current.push(slot);
            layer.addChild(c);
        }
    }

    // Reposition all slot text nodes to the current column x offsets. Called
    // once at init and any time the responsive column widths change. Cheap
    // — a handful of assignments per slot, no allocation.
    function applyLayoutToSlots() {
        const layout = colLayoutRef.current;
        for (const slot of slotsRef.current) {
            for (let i = 0; i < layout.xs.length; i++) {
                if (slot.texts[i]) slot.texts[i].x = layout.xs[i] + 10;
            }
        }
    }

    // Assign logical row indices to physical slots based on scrollTop, then
    // draw/update each. This is the hot path — should be O(visibleRows).
    function layout() {
        const app = appRef.current;
        const slots = slotsRef.current;
        const rows = rowsRef.current;
        if (!app || slots.length === 0) return;

        const scrollTop = scrollTopRef.current;
        const firstVisible = Math.max(0, Math.floor(scrollTop / ROW_H) - OVERSCAN);
        const capacity = slots.length;

        for (let i = 0; i < capacity; i++) {
            const rowIdx = firstVisible + i;
            const slot = slots[i];
            const row = rows[rowIdx];
            if (!row) {
                slot.container.visible = false;
                slot.rowIndex = -1;
                continue;
            }
            slot.container.visible = true;
            slot.container.y = rowIdx * ROW_H - scrollTop;
            if (slot.rowIndex !== rowIdx || slot.container.y !== rowIdx * ROW_H - scrollTop) {
                slot.rowIndex = rowIdx;
                paintRow(slot, row, rowIdx === hoveredIdxRef.current);
            } else if (rowIdx === hoveredIdxRef.current) {
                paintBg(slot, true);
            }
        }

        // Infinite-scroll trigger: when we're within ~8 rows of the end,
        // ask for more. Mirrors the DOM grid's condition.
        const lastVisible = firstVisible + capacity;
        if (lastVisible >= rows.length - 8 && query.hasNextPage && !query.isFetchingNextPage) {
            query.fetchNextPage();
        }
    }

    function paintRow(slot: RowSlot, row: InventoryRow, hovered: boolean) {
        paintBg(slot, hovered);

        for (let i = 0; i < COLS_BASE.length; i++) {
            const [key] = COLS_BASE[i];
            const t = slot.texts[i];
            const raw = row[key] as string | null | undefined;
            if (key === "status") {
                t.text = ""; // pill replaces the text
                continue;
            }
            if (key === "createdAt" || key === "updatedAt") {
                t.text = fmtDate(raw);
            } else if (key === "assignee") {
                t.text = raw ?? "—";
            } else if (key === "productCategory") {
                t.text = (raw ?? "").toUpperCase();
            } else {
                t.text = raw ?? "";
            }
            // Truncate visually — canvas doesn't do overflow:ellipsis. We
            // just clip via a mask on the row container in a future pass;
            // for the prototype, long strings extend into the next cell.
        }

        // Status pill — column 3 in COLS_BASE. x follows the responsive layout.
        const status = row.status ?? "";
        const c = STATUS_COLORS[status] ?? { bg: 0xe2e8f0, fg: 0x334155 };
        const pillW = 78, pillH = 18;
        const pillX = colLayoutRef.current.xs[3] + 10;
        const pillY = (ROW_H - pillH) / 2;
        slot.pillBg.clear();
        slot.pillBg.roundRect(pillX, pillY, pillW, pillH, 9).fill(c.bg);
        slot.pillText.text = status;
        slot.pillText.style.fill = c.fg;
        slot.pillText.x = pillX + (pillW - slot.pillText.width) / 2;
        slot.pillText.y = pillY + (pillH - slot.pillText.height) / 2;
    }

    function paintBg(slot: RowSlot, hovered: boolean) {
        const w = colLayoutRef.current.total;
        slot.bg.clear();
        slot.bg.rect(0, 0, w, ROW_H).fill(hovered ? HOVER_BG : ROW_STRIPE);
        // Bottom border.
        slot.bg.rect(0, ROW_H - 1, w, 1).fill(BORDER);
    }

    const fmtDate = (v: string | null | undefined): string => {
        if (!v) return "";
        try {
            return new Date(v).toLocaleString(undefined, {
                month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
            });
        } catch { return v ?? ""; }
    };

    function rowAtY(y: number): number {
        const idx = Math.floor((y + scrollTopRef.current) / ROW_H);
        if (idx < 0 || idx >= rowsRef.current.length) return -1;
        return idx;
    }

    function handleWheel(e: React.WheelEvent<HTMLDivElement>) {
        const total = rowsRef.current.length * ROW_H;
        const max = Math.max(0, total - viewportHRef.current);
        scrollTopRef.current = Math.min(max, Math.max(0, scrollTopRef.current + e.deltaY));
        layout();
    }

    function handleMove(e: React.MouseEvent<HTMLDivElement>) {
        const rect = e.currentTarget.getBoundingClientRect();
        const idx = rowAtY(e.clientY - rect.top);
        if (idx === hoveredIdxRef.current) return;
        const prev = hoveredIdxRef.current;
        hoveredIdxRef.current = idx;
        // Repaint the two affected slots only.
        for (const s of slotsRef.current) {
            if (s.rowIndex === prev || s.rowIndex === idx) {
                paintBg(s, s.rowIndex === idx);
            }
        }
        if (idx >= 0) {
            const row = rowsRef.current[idx];
            setHoverAnnouncement(`Row ${idx + 1}: ${row.serviceNumber} ${row.productName}`);
        }
    }

    function handleLeave() {
        const prev = hoveredIdxRef.current;
        if (prev < 0) return;
        hoveredIdxRef.current = -1;
        for (const s of slotsRef.current) if (s.rowIndex === prev) paintBg(s, false);
    }

    function handleClick(e: React.MouseEvent<HTMLDivElement>) {
        if (!onRowSelect) return;
        const rect = e.currentTarget.getBoundingClientRect();
        const idx = rowAtY(e.clientY - rect.top);
        if (idx < 0) return;
        const row = rowsRef.current[idx];
        if (row) onRowSelect(row);
    }

    // Sort handler shared with DOM header — click on header cell toggles
    // sort direction for sortable columns. Header stays DOM so screen
    // readers can still see it and keyboard focus works.
    const currentSort = params.sort ?? "createdAt";
    const currentDir = params.dir ?? "desc";
    function onHeaderClick(key: keyof InventoryRow) {
        if (!SORTABLE.has(key as string)) return;
        const nextDir = currentSort === key && currentDir === "desc" ? "asc" : "desc";
        onParamsChange({ ...params, sort: key as ListParams["sort"], dir: nextDir });
    }

    const lastPage = query.data?.pages[query.data.pages.length - 1];

    return (
        <div className="flex flex-col h-full min-h-[480px] bg-white">
            <CountDisplay
                totalEstimate={lastPage?.totalEstimate}
                filteredCount={lastPage?.filteredCount}
                lastServerMs={lastPage?.tookMs}
                loaded={rows.length} />

            {/* Outer scroll container = horizontal scroll only (canvas owns
                vertical scroll). `min-h-0` on the flex chain is required so
                flex children can shrink below their intrinsic content height
                — without it the canvas host would push past the viewport. */}
            <div
                className="flex-1 border-t border-slate-200 overflow-x-auto flex flex-col min-h-0"
                data-testid="gpu-grid-scroll"
            >
                {/* Column of header + canvas. `w-full` fills the viewport when
                    it's wider than MIN_TABLE_WIDTH; `minWidth` pins the floor
                    below which horizontal scroll kicks in. */}
                <div
                    className="flex-1 flex flex-col min-h-0 w-full"
                    style={{ minWidth: MIN_TABLE_WIDTH }}
                >
                    <div
                        className="bg-slate-50 border-b border-slate-200 text-[11px] font-semibold tracking-wide text-slate-500 uppercase flex flex-none"
                    >
                        {COLS_BASE.map(([key, header], i) => {
                            const sortable = SORTABLE.has(key as string);
                            const sorted = currentSort === key;
                            return (
                                <div
                                    key={key as string}
                                    onClick={sortable ? () => onHeaderClick(key) : undefined}
                                    style={{ width: colLayout.widths[i] }}
                                    className={`px-3 py-2.5 border-r border-slate-200 last:border-r-0 whitespace-nowrap select-none ${
                                        sortable ? "cursor-pointer hover:bg-slate-100" : ""
                                    }`}
                                    data-testid={`th-${key as string}`}
                                >
                                    {header}
                                    {sorted ? (currentDir === "asc" ? " ↑" : " ↓") : ""}
                                </div>
                            );
                        })}
                    </div>

                    {/* Canvas host is ALWAYS mounted so hostRef is populated
                        before the PIXI init effect runs. Previously we
                        conditionally rendered a "Loading…" div in its place
                        while the first query was pending, which meant a
                        page refresh (empty react-query cache → isPending=true
                        on mount) caused hostRef.current to be null when the
                        init effect fired — PIXI never initialised, and even
                        after rows arrived the canvas stayed blank. The
                        loading/error/empty states are now positioned overlays
                        on top of the (still-mounted) canvas. */}
                    <div className="flex-1 min-h-0 relative">
                        <div
                            ref={hostRef}
                            onWheel={handleWheel}
                            onMouseMove={handleMove}
                            onMouseLeave={handleLeave}
                            onClick={handleClick}
                            data-testid="gpu-grid-canvas-host"
                            className="absolute inset-0 cursor-pointer"
                        />
                        {(query.isPending || query.isError) && (
                            <div
                                className={`absolute inset-0 flex items-center justify-center px-4 py-6 text-sm z-10 ${
                                    query.isError ? "bg-red-50 text-red-700" : "bg-white/95 text-slate-500"
                                }`}
                            >
                                {query.isPending ? "Loading…" : query.error.message}
                            </div>
                        )}
                        {!query.isPending && !query.isError && rows.length === 0 && (
                            <div className="absolute inset-0 z-10 overflow-y-auto bg-white/95">
                                <EmptyState
                                    params={params}
                                    onParamsChange={onParamsChange}
                                    role={role}
                                    onCreateNew={onCreateNew}
                                />
                            </div>
                        )}
                    </div>
                    {query.isFetchingNextPage && (
                        <div className="px-4 py-3 text-xs text-slate-500 flex-none">Loading more…</div>
                    )}
                </div>
            </div>

            {/* SR-only live region so keyboard/screen-reader users get some
                signal from an otherwise opaque canvas. Prototype-grade. */}
            <div className="sr-only" aria-live="polite" aria-atomic="true">
                {hoverAnnouncement}
            </div>
        </div>
    );
}
