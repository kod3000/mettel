import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ApiError } from "../api/client.js";
import { useApi } from "../api/context.js";
import { createInventory, type CreateRequest, type InventoryRow } from "../api/inventory.js";

const CATEGORIES = ["voice", "data", "wireless", "other"] as const;
const INITIAL_STATUSES = ["pending", "active"] as const;

interface Props { open: boolean; onClose: () => void; }

export function CreateInventoryModal({ open, onClose }: Props) {
    const client = useApi();
    const qc = useQueryClient();
    const [form, setForm] = useState<CreateRequest>({
        serviceNumber: "",
        productCategory: "voice",
        productName: "",
        status: "pending",
    });
    const [errors, setErrors] = useState<Record<string, string[]>>({});

    const mut = useMutation<InventoryRow, ApiError, CreateRequest>({
        mutationFn: (body) => createInventory(client, body),
        onSuccess: () => {
            qc.invalidateQueries({ queryKey: ["inventory", "list"] });
            onClose();
        },
        onError: (err) => {
            const map = { ...(err.problem.errors ?? {}) };
            if (err.slug === "duplicate-service-number" && !map.serviceNumber) {
                map.serviceNumber = [err.problem.detail ?? err.problem.title ?? "Duplicate service number"];
            }
            setErrors(map);
        },
    });

    if (!open) return null;

    return (
        <div
            className="fixed inset-0 z-20 flex items-center justify-center bg-slate-900/40 backdrop-blur-sm"
            onClick={onClose}
        >
            <form
                onClick={(e) => e.stopPropagation()}
                onSubmit={(e) => { e.preventDefault(); setErrors({}); mut.mutate(form); }}
                className="w-[420px] rounded-lg bg-white p-5 shadow-xl flex flex-col gap-3"
            >
                <div>
                    <h2 className="text-base font-semibold text-slate-900">New inventory</h2>
                    <p className="text-xs text-slate-500">Server errors map to the field they belong to.</p>
                </div>

                <Field label="Service number" name="serviceNumber" error={errors.serviceNumber?.[0]}>
                    <input
                        type="text" required autoFocus
                        value={form.serviceNumber ?? ""}
                        onChange={(e) => setForm({ ...form, serviceNumber: e.target.value })}
                        data-testid="create-serviceNumber"
                        className={inputClasses(errors.serviceNumber)} />
                </Field>

                <Field label="Product category" name="productCategory" error={errors.productCategory?.[0]}>
                    <select
                        value={form.productCategory ?? "voice"}
                        onChange={(e) => setForm({ ...form, productCategory: e.target.value })}
                        className={inputClasses(errors.productCategory)}
                    >
                        {CATEGORIES.map((c) => <option key={c} value={c}>{c}</option>)}
                    </select>
                </Field>

                <Field label="Product name" name="productName" error={errors.productName?.[0]}>
                    <input
                        type="text" required
                        value={form.productName ?? ""}
                        onChange={(e) => setForm({ ...form, productName: e.target.value })}
                        className={inputClasses(errors.productName)} />
                </Field>

                <Field label="Initial status" name="status" error={errors.status?.[0]}>
                    <select
                        value={form.status ?? "pending"}
                        onChange={(e) => setForm({ ...form, status: e.target.value })}
                        className={inputClasses(errors.status)}
                    >
                        {INITIAL_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
                    </select>
                </Field>

                {mut.isError && Object.keys(errors).length === 0 && (
                    <div className="text-xs text-rose-700 bg-rose-50 border border-rose-200 rounded px-2 py-1">
                        {mut.error?.problem?.title ?? "Something went wrong."}
                    </div>
                )}

                <div className="flex gap-2 justify-end mt-2">
                    <button
                        type="button"
                        onClick={onClose}
                        className="rounded-md px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-100"
                    >
                        Cancel
                    </button>
                    <button
                        type="submit"
                        disabled={mut.isPending}
                        className="rounded-md bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white shadow-sm hover:bg-indigo-500 disabled:bg-indigo-400 disabled:cursor-not-allowed"
                    >
                        {mut.isPending ? "Creating…" : "Create"}
                    </button>
                </div>
            </form>
        </div>
    );
}

function Field({ label, name, error, children }: {
    label: string; name: string; error?: string; children: React.ReactNode;
}) {
    return (
        <label htmlFor={name} className="flex flex-col gap-1 text-xs">
            <span className="text-slate-700 font-medium">{label}</span>
            {children}
            {error && (
                <span
                    className="text-[11px] text-rose-700"
                    data-testid={`error-${name}`}
                >
                    {error}
                </span>
            )}
        </label>
    );
}

function inputClasses(err?: string[]): string {
    const base = "rounded-md border bg-white px-2.5 py-1.5 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-1";
    return err
        ? `${base} border-rose-400 focus:border-rose-500 focus:ring-rose-500`
        : `${base} border-slate-300 focus:border-indigo-500 focus:ring-indigo-500`;
}
