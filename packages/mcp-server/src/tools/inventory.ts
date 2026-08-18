import { z } from "zod";
import type { BruinConfig } from "../client.js";
import { request } from "../client.js";

// Shared vocabularies — mirrored from Domain/Inventory.cs and asserted by
// the OpenAPI enum patch in Program.cs. Keeping them literal here means an
// agent sees the exact set in the schema instead of a bare `string`.
const STATUSES = ["pending", "active", "disconnected"] as const;
const CATEGORIES = ["voice", "data", "wireless", "other"] as const;
const SORT_KEYS = ["createdAt", "updatedAt", "status", "serviceNumber", "productName"] as const;
const SEARCH_FIELDS = [
    "productName", "serviceNumber", "city", "state", "address", "assignee", "notes",
] as const;

export const inventorySearchTool = {
    name: "inventory_search",
    title: "Search / list inventory rows",
    description:
        "Search inventory with keyset pagination. Prefer narrow filters over pulling every row — this endpoint runs against a 5M-row table. Repeat the same call with the returned `nextCursor` to page; do NOT construct cursors yourself (they are HMAC-signed and tenant-scoped). Change any filter and the previous cursor becomes stale (400 cursor-stale) — start a new search instead. `fields` narrows the free-text search to specific columns; leave empty for the fastest broad tsvector match.",
    inputSchema: z.object({
        q: z.string().optional().describe("Free-text query. Case-insensitive, prefix-anchored per word."),
        fields: z.array(z.enum(SEARCH_FIELDS)).optional().describe(
            "Narrow `q` to these columns only. Omit to search all indexed columns (fastest).",
        ),
        status: z.array(z.enum(STATUSES)).optional().describe("OR set of status values to include."),
        productCategory: z.array(z.enum(CATEGORIES)).optional().describe("OR set of categories to include."),
        state: z.array(z.string()).optional().describe("OR set of US state codes (e.g. NY, CA)."),
        sort: z.enum(SORT_KEYS).optional().describe("Sort column. Defaults to createdAt."),
        dir: z.enum(["asc", "desc"]).optional().describe("Sort direction. Defaults to desc."),
        pageSize: z.number().int().min(1).max(500).optional().describe(
            "Rows per page. Defaults to 100. Max 500. Prefer smaller pages when scanning.",
        ),
        cursor: z.string().optional().describe(
            "Opaque cursor from a previous response's `nextCursor`. Pass verbatim; never edit.",
        ),
    }),
    async run(args: Record<string, unknown>, cfg: BruinConfig) {
        return request(cfg, {
            path: "/api/v1/inventory",
            query: {
                q: args.q as string | undefined,
                fields: args.fields as string[] | undefined,
                status: args.status as string[] | undefined,
                productCategory: args.productCategory as string[] | undefined,
                state: args.state as string[] | undefined,
                sort: args.sort as string | undefined,
                dir: args.dir as string | undefined,
                pageSize: args.pageSize as number | undefined,
                cursor: args.cursor as string | undefined,
            },
        });
    },
};

export const inventoryGetTool = {
    name: "inventory_get",
    title: "Fetch one inventory row by id",
    description:
        "Fetch a single row by its UUID. 404 (not-found) means the row is either non-existent, soft-deleted, or belongs to another tenant — the API deliberately leaks nothing.",
    inputSchema: z.object({
        id: z.string().uuid().describe("Row id (UUIDv7)."),
    }),
    async run(args: { id: string }, cfg: BruinConfig) {
        return request(cfg, { path: `/api/v1/inventory/${encodeURIComponent(args.id)}` });
    },
};

export const inventoryCreateTool = {
    name: "inventory_create",
    title: "Create a new inventory row",
    description:
        "Insert a new row. Requires the `admin` or `worker` role — call `me` first if unsure. `serviceNumber` is unique per tenant; a collision returns 409 with slug `duplicate-service-number` and the offending field name in `errors`. All optional fields default to null.",
    inputSchema: z.object({
        serviceNumber: z.string().min(1).describe("Unique per tenant. Free-form string; no format enforced."),
        productCategory: z.enum(CATEGORIES),
        productName: z.string().min(1),
        status: z.enum(STATUSES).describe("Initial status. Transitions via inventory_change_status."),
        city: z.string().optional(),
        state: z.string().optional().describe("Two-letter US state code."),
        address: z.string().optional(),
        assignee: z.string().optional().describe("Admin-only in the default field_policy — check `me.adminOnlyFields`."),
        notes: z.string().optional().describe("Admin-only in the default field_policy — check `me.adminOnlyFields`."),
    }),
    async run(args: Record<string, unknown>, cfg: BruinConfig) {
        return request(cfg, { method: "POST", path: "/api/v1/inventory", body: args });
    },
};

export const inventoryUpdateTool = {
    name: "inventory_update",
    title: "Patch fields on an existing row",
    description:
        "Partial update. Include ONLY the fields you want to change (plus `rowVersion` for optimistic concurrency). Sending `null` for a nullable field clears it; omitting a field leaves it untouched. Status is NOT accepted here — use `inventory_change_status` for the FSM. Admin-only fields (see `me.adminOnlyFields`) rejected for the `worker` role with a per-field error map.",
    inputSchema: z.object({
        id: z.string().uuid(),
        rowVersion: z.number().int().describe("The `rowVersion` returned by the last read. Bumped on every UPDATE; a stale value returns 409 concurrency-conflict."),
        serviceNumber: z.string().optional(),
        productCategory: z.enum(CATEGORIES).optional(),
        productName: z.string().optional(),
        city: z.string().nullable().optional(),
        state: z.string().nullable().optional(),
        address: z.string().nullable().optional(),
        assignee: z.string().nullable().optional().describe("Admin-only by default."),
        notes: z.string().nullable().optional().describe("Admin-only by default."),
    }),
    async run(args: Record<string, unknown>, cfg: BruinConfig) {
        const { id, ...patch } = args;
        return request(cfg, { method: "PATCH", path: `/api/v1/inventory/${encodeURIComponent(id as string)}`, body: patch });
    },
};

export const inventoryChangeStatusTool = {
    name: "inventory_change_status",
    title: "Transition a row's status through the FSM",
    description:
        "Legal transitions: pending → active, pending → disconnected, active → disconnected. Any other transition returns 400 with slug `invalid-status-transition`. Include `rowVersion` from the last read for optimistic concurrency.",
    inputSchema: z.object({
        id: z.string().uuid(),
        status: z.enum(STATUSES).describe("Target status. Must be reachable from the current status per the FSM."),
        rowVersion: z.number().int().describe("Optimistic-concurrency token from the last read."),
    }),
    async run(args: { id: string; status: string; rowVersion: number }, cfg: BruinConfig) {
        return request(cfg, {
            method: "PATCH",
            path: `/api/v1/inventory/${encodeURIComponent(args.id)}/status`,
            body: { status: args.status, rowVersion: args.rowVersion },
        });
    },
};

export const inventoryDeleteTool = {
    name: "inventory_delete",
    title: "Soft-delete a row (admin only)",
    description:
        "Soft-delete: sets `deleted_at`. Deleted rows are hidden from all reads (they 404) and free their `serviceNumber` for re-import. Requires the `admin` role — worker/reader receive 403. Not reversible via the API today.",
    inputSchema: z.object({
        id: z.string().uuid(),
    }),
    async run(args: { id: string }, cfg: BruinConfig) {
        await request(cfg, { method: "DELETE", path: `/api/v1/inventory/${encodeURIComponent(args.id)}` });
        return { ok: true, id: args.id };
    },
};
