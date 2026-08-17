import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useApi } from "../api/context.js";
import { createSavedView, deleteSavedView, listSavedViews, type SavedView } from "../api/savedViews.js";
import type { ListParams } from "../api/inventory.js";
import { reportApiError } from "../api/reportError.js";

interface Props {
    params: ListParams;
    onApply: (next: ListParams) => void;
    // Reader keys 403 on POST/PUT/DELETE saved-views; hide the Save input +
    // per-view delete × so read-only tenants can still apply an existing view.
    canWrite: boolean;
}

const CACHE_KEY = "bruin.lastSavedViewId";

export function SavedViewsBar({ params, onApply, canWrite }: Props) {
    const client = useApi();
    const qc = useQueryClient();
    const [name, setName] = useState("");

    const list = useQuery({
        queryKey: ["saved-views"],
        queryFn: () => listSavedViews(client),
        staleTime: 30_000,
    });

    const create = useMutation({
        mutationFn: () => createSavedView(client, {
            name: name.trim(),
            filters: JSON.stringify(params),
            sort: JSON.stringify({ sort: params.sort, dir: params.dir }),
            columns: JSON.stringify({}),
        }),
        onSuccess: (v) => {
            setName("");
            localStorage.setItem(CACHE_KEY, v.id!);
            qc.invalidateQueries({ queryKey: ["saved-views"] });
        },
        onError: (err) => reportApiError(err, { context: "Save view failed" }),
    });

    const del = useMutation({
        mutationFn: (id: string) => deleteSavedView(client, id),
        onSuccess: () => qc.invalidateQueries({ queryKey: ["saved-views"] }),
        onError: (err) => reportApiError(err, { context: "Delete view failed" }),
    });

    const apply = (v: SavedView) => {
        try {
            const restored = typeof v.filters === "string"
                ? JSON.parse(v.filters as string) as ListParams
                : (v.filters as unknown as ListParams);
            onApply(restored ?? {});
            localStorage.setItem(CACHE_KEY, v.id!);
        } catch { /* ignore malformed payload */ }
    };

    return (
        <div className="flex flex-wrap items-center gap-2 border-b border-slate-200 bg-slate-50 px-4 py-2 text-xs">
            <span className="text-slate-500">Saved views</span>
            {list.data?.views.map((v) => (
                <span
                    key={v.id}
                    className="inline-flex items-center gap-1 rounded-full bg-white ring-1 ring-slate-200 px-2 py-0.5"
                >
                    <button
                        type="button"
                        onClick={() => apply(v)}
                        data-testid={`view-apply-${v.name}`}
                        className="font-medium text-slate-700 hover:text-indigo-700"
                    >
                        {v.name}
                    </button>
                    {canWrite && (
                        <button
                            type="button"
                            onClick={() => del.mutate(v.id!)}
                            title="Delete"
                            className="text-slate-400 hover:text-rose-500"
                        >
                            ×
                        </button>
                    )}
                </span>
            ))}
            {list.data && list.data.views.length === 0 && (
                <span className="text-slate-400 italic">none yet</span>
            )}
            <div className="flex-1" />
            {canWrite && (
                <>
                    <input
                        type="text"
                        placeholder="Save current filters as…"
                        value={name}
                        onChange={(e) => setName(e.target.value)}
                        className="w-52 rounded-md border border-slate-300 bg-white px-2 py-1 text-xs focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                    />
                    <button
                        type="button"
                        disabled={!name.trim() || create.isPending}
                        onClick={() => create.mutate()}
                        className="rounded-md bg-slate-800 px-2.5 py-1 text-xs font-medium text-white shadow-sm hover:bg-slate-700 disabled:bg-slate-300 disabled:cursor-not-allowed"
                    >
                        Save
                    </button>
                </>
            )}
        </div>
    );
}
