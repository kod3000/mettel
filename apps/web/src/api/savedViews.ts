// Typed saved-views API wrapper. Filters/sort/columns are shipped as raw
// JSON strings on the wire — the API just persists them and the UI owns the
// shape, so callers pass whatever their view state looks like today.

import type { components } from "@bruin/api-types";
import type { ApiClient, RequestOptions } from "./client.js";

export type SavedView = components["schemas"]["SavedViewResponse"];
export type SavedViewList = components["schemas"]["SavedViewList"];
export type SavedViewUpsert = components["schemas"]["SavedViewUpsert"];

export async function listSavedViews(
    client: ApiClient, opts?: Pick<RequestOptions, "signal">): Promise<SavedViewList> {
    return await client.get<SavedViewList>("/api/v1/saved-views", opts);
}

export async function createSavedView(
    client: ApiClient, body: SavedViewUpsert, opts?: Pick<RequestOptions, "signal">): Promise<SavedView> {
    return await client.post<SavedView>("/api/v1/saved-views", body, opts);
}

export async function deleteSavedView(
    client: ApiClient, id: string, opts?: Pick<RequestOptions, "signal">): Promise<void> {
    await client.request<void>(`/api/v1/saved-views/${encodeURIComponent(id)}`, { ...opts, method: "DELETE" });
}
