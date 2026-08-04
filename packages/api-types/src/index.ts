// Public entry for @bruin/api-types.
//
// `generated.ts` is emitted by `openapi-typescript` from
// `apps/api/openapi.v1.json` — do not edit it by hand. Any type the frontend
// imports must reach it through this file so codegen breakage is a single
// import failure, not a spray across the codebase.

export * from "./generated.js";
export type { Problem, ProblemBySlug, ProblemSlug, InventoryRow, Cursor } from "./aliases.js";
export { slugOf } from "./aliases.js";
