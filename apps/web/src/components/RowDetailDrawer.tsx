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
import { useEffect, useState } from "react";
import { ApiError } from "../api/client.js";
import { useApi } from "../api/context.js";
import { toast } from "./Toaster.js";
import { reportApiError } from "../api/reportError.js";
import {
    deleteInventory,
    detailQueryKey,
    getInventory,
    patchInventory,
    patchStatus,
    type InventoryPatch,
    type InventoryRow,
    type StatusChangeResponse,
} from "../api/inventory.js";

interface Props {
    id: string;
    onClose: () => void;
    // Reader keys 403 on PATCH status; hide the entire "Change status"
    // section so read-only tenants can inspect a row without a dead panel.
    canWrite: boolean;
    // Admin-only: shows the Delete button (which soft-deletes the row).
    canDelete: boolean;
    // Fields the current role is not allowed to write per field_policy.
    // Inputs for these are rendered read-only in edit mode.
    adminOnlyFields: string[];
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

export function RowDetailDrawer({ id, onClose, canWrite, canDelete, adminOnlyFields }: Props) {
    const client = useApi();
    const qc = useQueryClient();

    // Edit-mode local state. `draft` holds only the fields the user has
    // touched (undefined = "leave as-is on save"); `fieldErrors` mirrors
    // ProblemDetails.Extensions.errors from a failed save so inputs can
    // highlight per-field messages inline.
    const [editing, setEditing] = useState(false);
    const [draft, setDraft] = useState<Record<string, string | null>>({});
    const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
    const [confirmDelete, setConfirmDelete] = useState(false);

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

    const patchMut = useMutation<InventoryRow, ApiError, InventoryPatch>({
        mutationFn: (body) => patchInventory(client, id, body),
        retry: shouldRetry,
        retryDelay,
        onSuccess: async () => {
            setEditing(false);
            setDraft({});
            setFieldErrors({});
            await Promise.all([
                qc.invalidateQueries({ queryKey: detailQueryKey(id) }),
                qc.invalidateQueries({ queryKey: ["inventory", "list"] }),
            ]);
        },
        onError: async (err) => {
            const errs = reportApiError(err, { context: "Save failed" });
            if (errs) setFieldErrors(errs);
            if (err.isSlug("concurrency-conflict")) {
                await qc.invalidateQueries({ queryKey: detailQueryKey(id) });
            }
        },
    });

    const delMut = useMutation<void, ApiError>({
        mutationFn: () => deleteInventory(client, id),
        onSuccess: async () => {
            toast.info("Row deleted.");
            await qc.invalidateQueries({ queryKey: ["inventory", "list"] });
            onClose();
        },
        onError: (err) => reportApiError(err, { context: "Delete failed" }),
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
    const busy = mut.isPending || patchMut.isPending || delMut.isPending;
    const backdropClose = () => { if (!busy && !editing) onClose(); };
    const readOnlySet = new Set(adminOnlyFields);
    const draftValue = (k: keyof InventoryRow): string =>
        (draft[k as string] ?? (row?.[k] as string | null | undefined) ?? "") as string;
    const setField = (k: string, v: string | null) => {
        setDraft((d) => ({ ...d, [k]: v }));
        setFieldErrors((e) => { const { [k]: _drop, ...rest } = e; return rest; });
    };
    const startEdit = () => { setDraft({}); setFieldErrors({}); setEditing(true); };
    const cancelEdit = () => { setDraft({}); setFieldErrors({}); setEditing(false); };
    const saveEdit = () => {
        if (!row) return;
        // Only send fields that actually changed vs. the current row.
        const body: Record<string, unknown> = { rowVersion: row.rowVersion };
        for (const [k, v] of Object.entries(draft)) {
            const currentVal = (row[k as keyof InventoryRow] as string | null | undefined) ?? null;
            // Empty string on an optional field = clear (null); on a
            // required field it's caught by server-side validation.
            const normalized = v === "" ? null : v;
            if (normalized !== currentVal) body[k] = normalized;
        }
        if (Object.keys(body).length === 1) { // only rowVersion — no changes
            cancelEdit();
            return;
        }
        patchMut.mutate(body as unknown as InventoryPatch);
    };

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
                        editing ? (
                            <EditForm
                                row={row}
                                draftValue={draftValue}
                                setField={setField}
                                fieldErrors={fieldErrors}
                                readOnlySet={readOnlySet}
                                saving={patchMut.isPending}
                                onSave={saveEdit}
                                onCancel={cancelEdit}
                            />
                        ) : (
                            <>
                                <FieldRow label="Service #">
                                    <div className="text-sm font-mono text-slate-800">{row.serviceNumber}</div>
                                </FieldRow>
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

                                {/* Row actions visible for readers but
                                    disabled — reviewer letter: teach the
                                    permission model, don't hide it. */}
                                <div className="mt-2 border-t border-slate-200 pt-3 flex flex-wrap gap-2">
                                    <button
                                        type="button"
                                        data-testid="btn-edit"
                                        onClick={startEdit}
                                        disabled={!canWrite}
                                        title={canWrite ? undefined : "Requires admin or worker role"}
                                        className="rounded-md px-3 py-1.5 text-xs font-medium border bg-white border-slate-300 text-slate-700 hover:bg-slate-50 disabled:opacity-50 disabled:cursor-not-allowed"
                                    >
                                        Edit fields
                                    </button>
                                    <button
                                        type="button"
                                        data-testid="btn-delete"
                                        onClick={() => setConfirmDelete(true)}
                                        disabled={!canDelete || delMut.isPending}
                                        title={canDelete ? undefined : "Requires admin role"}
                                        className="rounded-md px-3 py-1.5 text-xs font-medium border bg-white border-rose-300 text-rose-700 hover:bg-rose-50 disabled:opacity-50 disabled:cursor-not-allowed"
                                    >
                                        Delete row
                                    </button>
                                </div>
                                <ActionPanel row={row} mut={mut} canWrite={canWrite} />
                            </>
                        )
                    ) : null}
                    {confirmDelete && row && (
                        <ConfirmDelete
                            row={row}
                            pending={delMut.isPending}
                            onConfirm={() => { setConfirmDelete(false); delMut.mutate(); }}
                            onCancel={() => setConfirmDelete(false)}
                        />
                    )}
                </div>
            </aside>
        </div>
    );
}

function ActionPanel({
    row, mut, canWrite,
}: {
    row: InventoryRow;
    mut: ReturnType<typeof useMutation<StatusChangeResponse, ApiError, { status: string; rowVersion: number }>>;
    canWrite: boolean;
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
                            disabled={!canWrite || mut.isPending}
                            title={canWrite ? undefined : "Requires admin or worker role"}
                            onClick={() => mut.mutate({ status: s, rowVersion: row.rowVersion })}
                            className={buttonClass(s, !canWrite || mut.isPending)}
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

// Edit-mode form. Simple stacked inputs — one per writable column. Fields
// listed in adminOnlyFields render as read-only for the current role
// (workers) with a small "admin only" hint so the operator sees why they
// can't edit. Server-side field_policy still enforces this on save.
function EditForm({
    row, draftValue, setField, fieldErrors, readOnlySet, saving, onSave, onCancel,
}: {
    row: InventoryRow;
    draftValue: (k: keyof InventoryRow) => string;
    setField: (k: string, v: string | null) => void;
    fieldErrors: Record<string, string[]>;
    readOnlySet: Set<string>;
    saving: boolean;
    onSave: () => void;
    onCancel: () => void;
}) {
    const categories = ["voice", "data", "wireless", "other"] as const;
    return (
        <form
            onSubmit={(e) => { e.preventDefault(); onSave(); }}
            data-testid="row-edit-form"
            className="space-y-3"
        >
            <EditField label="Service #" name="serviceNumber" required
                value={draftValue("serviceNumber")}
                onChange={(v) => setField("serviceNumber", v)}
                readOnly={readOnlySet.has("serviceNumber")}
                errors={fieldErrors.serviceNumber} />
            <EditField label="Product name" name="productName" required
                value={draftValue("productName")}
                onChange={(v) => setField("productName", v)}
                readOnly={readOnlySet.has("productName")}
                errors={fieldErrors.productName} />
            <div>
                <label className="block text-[11px] uppercase tracking-wide text-slate-500 mb-1">
                    Category
                    {readOnlySet.has("productCategory") && <span className="ml-1 text-slate-400 lowercase italic">(admin only)</span>}
                </label>
                <select
                    value={draftValue("productCategory")}
                    disabled={readOnlySet.has("productCategory") || saving}
                    onChange={(e) => setField("productCategory", e.target.value)}
                    className="w-full rounded-md border border-slate-300 bg-white px-2 py-1 text-sm text-slate-800 disabled:bg-slate-100 disabled:text-slate-500"
                >
                    {categories.map((c) => <option key={c} value={c}>{c}</option>)}
                </select>
                {fieldErrors.productCategory?.map((m, i) => (
                    <div key={i} className="mt-1 text-xs text-rose-700">{m}</div>
                ))}
            </div>
            <EditField label="City" name="city"
                value={draftValue("city")}
                onChange={(v) => setField("city", v)}
                readOnly={readOnlySet.has("city")}
                errors={fieldErrors.city} />
            <EditField label="State" name="state"
                value={draftValue("state")}
                onChange={(v) => setField("state", v)}
                readOnly={readOnlySet.has("state")}
                errors={fieldErrors.state} />
            <EditField label="Address" name="address"
                value={draftValue("address")}
                onChange={(v) => setField("address", v)}
                readOnly={readOnlySet.has("address")}
                errors={fieldErrors.address} />
            <EditField label="Assignee" name="assignee"
                value={draftValue("assignee")}
                onChange={(v) => setField("assignee", v)}
                readOnly={readOnlySet.has("assignee")}
                errors={fieldErrors.assignee} />
            <div>
                <label className="block text-[11px] uppercase tracking-wide text-slate-500 mb-1">Notes</label>
                <textarea
                    value={draftValue("notes")}
                    disabled={readOnlySet.has("notes") || saving}
                    onChange={(e) => setField("notes", e.target.value)}
                    rows={3}
                    className="w-full rounded-md border border-slate-300 bg-white px-2 py-1 text-sm text-slate-800 disabled:bg-slate-100"
                />
                {fieldErrors.notes?.map((m, i) => (
                    <div key={i} className="mt-1 text-xs text-rose-700">{m}</div>
                ))}
            </div>
            <div className="text-[11px] text-slate-500">
                Editing row version <span className="font-mono">{row.rowVersion}</span>. A concurrent
                update will 409 and reload the drawer.
            </div>
            <div className="flex gap-2 pt-1">
                <button
                    type="submit"
                    data-testid="btn-save"
                    disabled={saving}
                    className="rounded-md bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white shadow-sm hover:bg-indigo-500 disabled:opacity-60"
                >
                    {saving ? "Saving…" : "Save"}
                </button>
                <button
                    type="button"
                    onClick={onCancel}
                    disabled={saving}
                    className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-60"
                >
                    Cancel
                </button>
            </div>
        </form>
    );
}

function EditField({
    label, name, value, onChange, readOnly, required, errors,
}: {
    label: string;
    name: string;
    value: string;
    onChange: (v: string) => void;
    readOnly?: boolean;
    required?: boolean;
    errors?: string[];
}) {
    return (
        <div>
            <label htmlFor={`edit-${name}`} className="block text-[11px] uppercase tracking-wide text-slate-500 mb-1">
                {label}
                {required && <span className="text-rose-600"> *</span>}
                {readOnly && <span className="ml-1 text-slate-400 lowercase italic">(admin only)</span>}
            </label>
            <input
                id={`edit-${name}`}
                type="text"
                value={value}
                readOnly={readOnly}
                onChange={(e) => onChange(e.target.value)}
                className="w-full rounded-md border border-slate-300 bg-white px-2 py-1 text-sm text-slate-800 read-only:bg-slate-100 read-only:text-slate-500"
            />
            {errors?.map((m, i) => (
                <div key={i} className="mt-1 text-xs text-rose-700">{m}</div>
            ))}
        </div>
    );
}

function ConfirmDelete({
    row, pending, onConfirm, onCancel,
}: {
    row: InventoryRow;
    pending: boolean;
    onConfirm: () => void;
    onCancel: () => void;
}) {
    return (
        <div
            role="alertdialog"
            aria-labelledby="confirm-delete-title"
            className="fixed inset-0 z-40 flex items-center justify-center bg-slate-900/40 p-4"
            onClick={onCancel}
        >
            <div
                onClick={(e) => e.stopPropagation()}
                className="w-full max-w-sm rounded-lg bg-white shadow-xl border border-slate-200 p-4"
            >
                <div id="confirm-delete-title" className="text-sm font-semibold text-slate-900">
                    Delete inventory row?
                </div>
                <div className="mt-1 text-xs text-slate-600">
                    <span className="font-mono">{row.serviceNumber}</span> — {row.productName}
                </div>
                <div className="mt-2 text-xs text-slate-500">
                    Soft delete: the row is hidden from lists but kept for audit. You can re-create
                    the same service number afterwards.
                </div>
                <div className="mt-4 flex justify-end gap-2">
                    <button
                        type="button"
                        onClick={onCancel}
                        disabled={pending}
                        className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-60"
                    >
                        Cancel
                    </button>
                    <button
                        type="button"
                        data-testid="btn-confirm-delete"
                        onClick={onConfirm}
                        disabled={pending}
                        className="rounded-md bg-rose-600 px-3 py-1.5 text-xs font-medium text-white shadow-sm hover:bg-rose-500 disabled:opacity-60"
                    >
                        {pending ? "Deleting…" : "Delete"}
                    </button>
                </div>
            </div>
        </div>
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
