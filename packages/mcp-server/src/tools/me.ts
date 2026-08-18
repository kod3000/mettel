import { z } from "zod";
import type { BruinConfig } from "../client.js";
import { request } from "../client.js";

// GET /api/v1/me — resolves the tenant, role, and per-field policy for the
// current API key. Cheap; agents should call this before writes so they know
// which fields are admin-only and can avoid a 403 round-trip.
export const meTool = {
    name: "me",
    title: "Who am I (tenant, role, admin-only fields)",
    description:
        "Return the tenant id, role, and admin-only field list for the current API key. Call this once at the start of a session so subsequent create/update tools know which fields are restricted. Also useful to sanity-check auth before taking any action.",
    inputSchema: z.object({}),
    async run(_: unknown, cfg: BruinConfig) {
        return request(cfg, { path: "/api/v1/me" });
    },
};
