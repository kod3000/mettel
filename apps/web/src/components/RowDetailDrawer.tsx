// Right-side detail drawer opened by row click. Two purposes:
//   1. Present the full row (fields the grid truncates or omits).
//   2. Offer the one write action the API exposes: PATCH /status, gated by
//      the same StatusTransitions.cs matrix the server enforces.
//
// API-tight behaviours to preserve:
//   - `rowVersion` from the detail read is sent back on PATCH; a 409
//     `concurrency-conflict` triggers a refetch so the operator sees the
//     latest state instead of retrying blind.
//   - Server error slugs map 1:1 to UI messaging via `slugOf` on ApiError.
//   - 5xx (server side had a hiccup) enters the "auto-recovery" state:
//     query/mutation retry with backoff, banner reflects attempt count.
//     4xx errors do NOT enter recovery — they're client-side and terminal.
//   - X-Write-LSN echo (handled by the client) is preserved so the next
//     list refetch reads through the primary if the replica hasn't caught up.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { ApiError } from "../api/client.js";
import { useApi } from "../api/context.js";
import {
    detailQueryKey,
    getInventory,
    patchStatus,
    type InventoryRow,
    type StatusChangeResponse,
} from "../api/inventory.js";

interface Props {
    id: string;
    onClose: () => void;
}

// Client-side mirror of apps/api/Domain/StatusTransitions.cs. Kept short
// enough that drift is obvious in review; the server is still the source of
// truth (409 invalid-status-transition catches any UI mistake).
const NEXT_STATUSES: Record<string, string[]> = {
    pending: ["active", "disconnected"],
    active: ["disconnected"],
    disconnected: [],
};

// Retry only 5xx — 4xx are terminal, tighter loop is wasted work + noise.
const shouldRetry = (failureCount: number, error: unknown): boolean => {
    if (failureCount >= 3) return false;
    if (error instanceof ApiError) return error.status >= 500;
    return true; // network errors: retry
};
const retryDelay = (attempt: number) => Math.min(1000 * 2 ** attempt, 8000);

export function RowDetailDrawer({ id, onClose }: Props) {
    const client = useApi();
    const qc = useQueryClient();

    const detail = useQuery<InventoryRow, ApiError>({
        queryKey: detailQueryKey(id),
        queryFn: ({ signal }) => getInventory(client, id, { signal }),
        retry: shouldRetry,
        retryDelay,
        staleTime: 0,
    });

    const mut = useMutation<StatusChangeResponse, ApiError, { status: string; rowVersion: number }>({
        mutationFn: ({ status, rowVersion }) =>
            patchStatus(client, id, { status, rowVersion }),
        retry: shouldRetry,
        retryDelay,
        onSuccess: async () => {
            // Refetch this row (new row_version + updated_at) and the list
            // (status column changes). LSN echo on the mutation response
            // already updated the client watermark, so the list refetch
            // reads at-or-after the write.
            await Promise.all([
                qc.invalidateQueries({ queryKey: detailQueryKey(id) }),
                qc.invalidateQueries({ queryKey: ["inventory", "list"] }),
            ]);
        },
        onError: async (err) => {
            // A 409 concurrency-conflict means our rowVersion is stale — pull
            // the latest so the operator can decide again with fresh data.
            if (err.isSlug("concurrency-conflict")) {
                await qc.invalidateQueries({ queryKey: detailQueryKey(id) });
            }
        },
    });

    // Esc-to-close; disabled while a mutation is in flight so the user can't
    // dismiss a request they're waiting on.
    useEffect(() => {
        const onKey = (e: KeyboardEvent) => {
            if (e.key === "Escape" && !mut.isPending) onClose();
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [mut.isPending, onClose]);

    const row = detail.data;
    const busy = mut.isPending;
    const backdropClose = () => { if (!busy) onClose(); };

    return (
        <div
            className="fixed inset-0 z-30 bg-slate-900/30"
            onClick={backdropClose}
            data-testid="drawer-backdrop"
        >
            <aside
                onClick={(e) => e.stopPropagation()}
                role="dialog"
                aria-label="Inventory detail"
                data-testid="row-drawer"
                className="fixed right-0 top-0 bottom-0 w-full sm:w-[440px] bg-white shadow-2xl border-l border-slate-200 flex flex-col"
            >
                <header className="flex items-start gap-3 px-4 py-3 border-b border-slate-200">
                    <div className="flex-1 min-w-0">
                        <div className="text-[11px] uppercase tracking-wide text-slate-500">
                            Inventory
                        </div>
                        <div className="text-sm font-semibold text-slate-900 truncate font-mono">
                            {row?.serviceNumber ?? "…"}
                        </div>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={busy}
                        className="rounded-md p-1 text-slate-500 hover:bg-slate-100 hover:text-slate-700 disabled:opacity-40 disabled:cursor-not-allowed"
                        aria-label="Close"
                    >
                        <CloseIcon />
                    </button>
                </header>

                <div className="flex-1 overflow-y-auto px-4 py-4 space-y-4">
                    <RecoveryBanner
                        detailFailures={detail.failureCount}
                        mutationFailures={mut.failureCount}
                        detailFetching={detail.isFetching}
                        mutationPending={mut.isPending}
                    />

                    {detail.isPending ? (
                        <SkeletonBody />
                    ) : detail.isError && !isRecovering(detail.failureCount, detail.isFetching) ? (
                        <TerminalError err={detail.error} onDismiss={onClose} />
                    ) : row ? (
                        <>
                            <FieldRow label="Product">
                                <div className="text-sm text-slate-900">{row.productName}</div>
                                <div className="text-[11px] uppercase tracking-wide text-slate-500">
                                    {row.productCategory}
                                </div>
                            </FieldRow>
                            <FieldRow label="Status">
                                <StatusBadge s={row.status} />
                            </FieldRow>
                            <FieldRow label="Assignee">
                                <div className="text-sm font-mono text-slate-700">{row.assignee ?? "—"}</div>
                            </FieldRow>
                            <FieldRow label="Location">
                                <div className="text-sm text-slate-800">
                                    {[row.city, row.state].filter(Boolean).join(", ") || "—"}
                                </div>
                                {row.address && (
                                    <div className="text-xs text-slate-500 mt-0.5">{row.address}</div>
                                )}
                            </FieldRow>
                            {row.notes && (
                                <FieldRow label="Notes">
                                    <div className="text-sm text-slate-800 whitespace-pre-wrap">{row.notes}</div>
                                </FieldRow>
                            )}
                            <FieldRow label="Audit">
                                <div className="text-xs text-slate-600 tabular-nums">
                                    <div>Created&nbsp;{fmt(row.createdAt)}</div>
                                    <div>Updated&nbsp;{fmt(row.updatedAt)}</div>
                                    <div>Row version&nbsp;<span className="font-mono">{row.rowVersion}</span></div>
                                </div>
                            </FieldRow>

                            <ActionPanel row={row} mut={mut} />
                        </>
                    ) : null}
                </div>
            </aside>
        </div>
    );
}

function ActionPanel({
    row, mut,
}: {
    row: InventoryRow;
    mut: ReturnType<typeof useMutation<StatusChangeResponse, ApiError, { status: string; rowVersion: number }>>;
}) {
    const nexts = NEXT_STATUSES[row.status] ?? [];
    const err = mut.error;
    const terminalErr = err && !isRecovering(mut.failureCount, mut.isPending);

    return (
        <div className="mt-2 border-t border-slate-200 pt-4">
            <div className="text-[11px] uppercase tracking-wide text-slate-500 mb-2">
                Change status
            </div>

            {nexts.length === 0 ? (
                <div className="text-xs text-slate-500 italic">
                    Terminal state — no further transitions allowed.
                </div>
            ) : (
                <div className="flex flex-wrap gap-2">
                    {nexts.map((s) => (
                        <button
                            key={s}
                            type="button"
                            data-testid={`btn-status-${s}`}
                            disabled={mut.isPending}
                            onClick={() => mut.mutate({ status: s, rowVersion: row.rowVersion })}
                            className={buttonClass(s, mut.isPending)}
                        >
                            {mut.isPending && mut.variables?.status === s ? "Working…" : `→ ${s}`}
                        </button>
                    ))}
                </div>
            )}

            {terminalErr && (
                <div
                    role="alert"
                    data-testid="mutation-error"
                    className="mt-3 rounded border border-rose-200 bg-rose-50 px-2.5 py-2 text-xs text-rose-800"
                >
                    <div className="font-semibold">
                        {mutationErrorTitle(err)}
                    </div>
                    <div className="mt-0.5 text-rose-700">
                        {err.problem.detail ?? err.problem.title ?? "Request failed."}
                    </div>
                    {err.isSlug("concurrency-conflict") && (
                        <div className="mt-1 text-[11px] text-rose-600">
                            Row was refreshed — try again.
                        </div>
                    )}
                </div>
            )}
        </div>
    );
}

// Auto-recovery banner: shown whenever a query/mutation is between attempts
// on a 5xx failure. Attempts count reflects failures so far (retry #1 = one
// prior failure, etc.). Signals to the operator that the system is handling
// the hiccup and they don't need to reload.
function RecoveryBanner({
    detailFailures, mutationFailures, detailFetching, mutationPending,
}: {
    detailFailures: number;
    mutationFailures: number;
    detailFetching: boolean;
    mutationPending: boolean;
}) {
    const detailRecovering = isRecovering(detailFailures, detailFetching);
    const mutRecovering = isRecovering(mutationFailures, mutationPending);
    if (!detailRecovering && !mutRecovering) return null;
    const attempt = Math.max(detailFailures, mutationFailures);

    return (
        <div
            role="status"
            data-testid="recovery-banner"
            className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 flex items-center gap-2"
        >
            <Spinner />
            <div className="flex-1">
                <div className="text-xs font-semibold text-amber-900">
                    System auto-recovery in progress
                </div>
                <div className="text-[11px] text-amber-800">
                    Backend returned an error — retrying (attempt {attempt + 1} of 4).
                </div>
            </div>
        </div>
    );
}

// True when there is at least one prior failure and TanStack is still
// working on it (either fetching for queries or pending for mutations).
// After all retries exhaust, isFetching/isPending flip to false and the
// error becomes terminal — we surface it inline instead of the banner.
function isRecovering(failureCount: number, active: boolean): boolean {
    return failureCount > 0 && active;
}

function mutationErrorTitle(err: ApiError): string {
    switch (err.slug) {
        case "invalid-status-transition": return "Transition not allowed";
        case "concurrency-conflict":       return "Row changed";
        case "not-found":                  return "Row no longer exists";
        case "validation-failed":          return "Validation failed";
        case "unauthorized":               return "Not authorized";
        default:                           return err.status >= 500 ? "Server error" : "Request failed";
    }
}

function buttonClass(target: string, pending: boolean): string {
    const base = "rounded-md px-3 py-1.5 text-xs font-medium border transition-colors";
    const disabled = "disabled:opacity-50 disabled:cursor-not-allowed";
    if (pending) return `${base} ${disabled} bg-slate-100 border-slate-300 text-slate-600`;
    if (target === "disconnected") {
        return `${base} ${disabled} bg-white border-rose-300 text-rose-700 hover:bg-rose-50`;
    }
    if (target === "active") {
        return `${base} ${disabled} bg-white border-emerald-300 text-emerald-700 hover:bg-emerald-50`;
    }
    return `${base} ${disabled} bg-white border-slate-300 text-slate-700 hover:bg-slate-50`;
}

function FieldRow({ label, children }: { label: string; children: React.ReactNode }) {
    return (
        <div className="grid grid-cols-[110px_1fr] gap-3 items-start">
            <div className="text-[11px] uppercase tracking-wide text-slate-500 pt-0.5">{label}</div>
            <div className="min-w-0">{children}</div>
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

function TerminalError({ err, onDismiss }: { err: ApiError; onDismiss: () => void }) {
    return (
        <div
            role="alert"
            className="rounded border border-rose-200 bg-rose-50 px-3 py-2.5 text-sm text-rose-800"
        >
            <div className="font-semibold">
                {err.status === 404 ? "Row not found" : err.problem.title ?? "Failed to load"}
            </div>
            {err.problem.detail && (
                <div className="mt-1 text-xs text-rose-700">{err.problem.detail}</div>
            )}
            <button
                type="button"
                onClick={onDismiss}
                className="mt-2 text-xs text-rose-700 underline hover:no-underline"
            >
                Close
            </button>
        </div>
    );
}

function SkeletonBody() {
    return (
        <div className="animate-pulse space-y-3">
            <div className="h-4 bg-slate-100 rounded w-1/3" />
            <div className="h-4 bg-slate-100 rounded w-2/3" />
            <div className="h-4 bg-slate-100 rounded w-1/2" />
            <div className="h-4 bg-slate-100 rounded w-3/4" />
        </div>
    );
}

function Spinner() {
    return (
        <svg
            className="h-4 w-4 animate-spin text-amber-700"
            viewBox="0 0 24 24" fill="none"
        >
            <circle cx="12" cy="12" r="10" stroke="currentColor" strokeOpacity="0.25" strokeWidth="3" />
            <path d="M22 12a10 10 0 0 1-10 10" stroke="currentColor" strokeWidth="3" strokeLinecap="round" />
        </svg>
    );
}

function CloseIcon() {
    return (
        <svg viewBox="0 0 20 20" className="h-4 w-4" fill="currentColor" aria-hidden="true">
            <path d="M4.293 4.293a1 1 0 0 1 1.414 0L10 8.586l4.293-4.293a1 1 0 1 1 1.414 1.414L11.414 10l4.293 4.293a1 1 0 0 1-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 0 1-1.414-1.414L8.586 10 4.293 5.707a1 1 0 0 1 0-1.414Z" />
        </svg>
    );
}

function fmt(v: string | null | undefined): string {
    if (!v) return "";
    try {
        return new Date(v).toLocaleString(undefined, {
            year: "numeric", month: "short", day: "numeric",
            hour: "numeric", minute: "2-digit",
        });
    } catch { return v; }
}
