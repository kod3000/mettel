// Typed wrapper over the raw fetch client for the inventory endpoints.
// Handlers in the UI import these functions, not the generic `client.request`,
// so callers stay type-safe against the OpenAPI-derived schemas.

import type { components } from "@bruin/api-types";
import type { ApiClient, RequestOptions } from "./client.js";

// Convenience aliases so hooks / components don't reach into `components`.
export type InventoryRow = components["schemas"]["InventoryRow"];
export type ListResponse = components["schemas"]["ListResponse"];
export type StatusChangeResponse = components["schemas"]["StatusChangeResponse"];
export type CreateRequest = components["schemas"]["CreateRequest"];
export type StatusPatch = components["schemas"]["StatusPatch"];

export type SortKey = "createdAt" | "updatedAt" | "status" | "serviceNumber" | "productName";
export type SortDir = "asc" | "desc";

export interface ListParams {
    q?: string;
    status?: string[];
    productCategory?: string[];
    state?: string[];
    sort?: SortKey;
    dir?: SortDir;
    pageSize?: number;
    cursor?: string;
}

// Canonical form the query hook uses in its key tuple. Kept alphabetical so
// two callers producing the same logical filter set yield the same key,
// which is what makes TanStack Query dedupe across React trees.
// Detail queries share this key prefix so the drawer's fetch and the
// post-mutation refetch land on the same cache slot.
export const detailQueryKey = (id: string) => ["inventory", "detail", id] as const;

export const listQueryKey = (p: ListParams) => [
    "inventory",
    "list",
    {
        q: p.q ?? null,
        status: [...(p.status ?? [])].sort(),
        productCategory: [...(p.productCategory ?? [])].sort(),
        state: [...(p.state ?? [])].sort(),
        sort: p.sort ?? "createdAt",
        dir: p.dir ?? "desc",
        pageSize: p.pageSize ?? 100,
    },
] as const;

export async function listInventory(
    client: ApiClient,
    params: ListParams,
    opts?: Pick<RequestOptions, "signal">,
): Promise<ListResponse> {
    return await client.get<ListResponse>("/api/v1/inventory", {
        query: {
            q: params.q,
            status: params.status,
            productCategory: params.productCategory,
            state: params.state,
            sort: params.sort,
            dir: params.dir,
            pageSize: params.pageSize,
            cursor: params.cursor,
        },
        signal: opts?.signal,
    });
}

export async function getInventory(
    client: ApiClient, id: string, opts?: Pick<RequestOptions, "signal">,
): Promise<InventoryRow> {
    return await client.get<InventoryRow>(`/api/v1/inventory/${encodeURIComponent(id)}`, opts);
}

export async function createInventory(
    client: ApiClient, body: CreateRequest, opts?: Pick<RequestOptions, "signal">,
): Promise<InventoryRow> {
    return await client.post<InventoryRow>("/api/v1/inventory", body, opts);
}

export async function patchStatus(
    client: ApiClient, id: string, body: StatusPatch, opts?: Pick<RequestOptions, "signal">,
): Promise<StatusChangeResponse> {
    return await client.patch<StatusChangeResponse>(
        `/api/v1/inventory/${encodeURIComponent(id)}/status`, body, opts);
}

// Arbitrary-field patch. Body: { rowVersion, [field]: value | null }.
// Fields absent from the body are left untouched; null clears (for
// nullable columns). Returns the full updated row.
export interface InventoryPatch {
    rowVersion: number;
    serviceNumber?: string;
    productCategory?: string;
    productName?: string;
    city?: string | null;
    state?: string | null;
    address?: string | null;
    assignee?: string | null;
    notes?: string | null;
}
export async function patchInventory(
    client: ApiClient, id: string, body: InventoryPatch, opts?: Pick<RequestOptions, "signal">,
): Promise<InventoryRow> {
    return await client.patch<InventoryRow>(
        `/api/v1/inventory/${encodeURIComponent(id)}`, body, opts);
}

// Soft-delete. 204 on success; 404 if the row was already deleted (idempotent).
export async function deleteInventory(
    client: ApiClient, id: string, opts?: Pick<RequestOptions, "signal">,
): Promise<void> {
    await client.del<void>(`/api/v1/inventory/${encodeURIComponent(id)}`, opts);
}
