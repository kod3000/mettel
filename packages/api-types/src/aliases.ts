// Aliases + narrowing helpers over the raw generated types.
//
// The generated types name paths + schemas verbatim from the OpenAPI doc;
// what the frontend actually wants is short, purposeful names. Keeping the
// aliases here (rather than in `apps/web`) means the frontend never
// hand-writes an API type — Phase 7 gate.

import type { components } from "./generated.js";

export type InventoryRow = components["schemas"]["InventoryRow"];
export type Cursor = string;

// Problem envelope union — indexed by the stable `type` slug the API emits.
// New slugs must be added on the server (Errors/ErrorSlugs.cs) AND here in
// lockstep. Callers switch on `slugOf(problem)`.
export type ProblemSlug =
    | "validation-failed"
    | "cursor-invalid"
    | "cursor-stale"
    | "invalid-status-transition"
    | "duplicate-service-number"
    | "concurrency-conflict"
    | "not-found"
    | "unsupported-media-type"
    | "payload-too-large"
    | "unauthorized"
    | "forbidden";

// Alias over the generated ProblemDetails so callers get one type name and
// codegen owns the field shape. `errors` (the per-field map on validation
// failures) is declared on the generated schema via the AddSchemaTransformer
// patch in apps/api/Program.cs, so no hand-widening is needed here.
export type Problem = components["schemas"]["ProblemDetails"];

export type ProblemBySlug<S extends ProblemSlug> = Problem & { __slug: S };

export function slugOf(p: Problem): ProblemSlug | null {
    if (!p?.type) return null;
    const last = p.type.split("/").pop();
    if (!last) return null;
    return last as ProblemSlug;
}
