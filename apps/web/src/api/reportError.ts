import { ApiError } from "./client.js";
import { toast } from "../components/Toaster.js";

// Split behavior for API mutation failures:
//   - ProblemDetails with a populated `errors` map → return the map so
//     the caller can render per-field messages inline. NO toast (the
//     field-level UI is the primary channel).
//   - Everything else (5xx, network, ProblemDetails without errors,
//     non-ApiError throws) → toast with the best available message.
//
// The `context` is prepended verbatim: "Create failed — Duplicate …".
// Keep it short; the toaster line-wraps but users skim the first phrase.
//
// Returns the field-error map when applicable, otherwise null.
export function reportApiError(
    err: unknown,
    opts?: { context?: string },
): Record<string, string[]> | null {
    const prefix = opts?.context ? `${opts.context} — ` : "";

    if (!(err instanceof ApiError)) {
        const msg = err instanceof Error ? err.message : "Unexpected error";
        toast.error(`${prefix}${msg}`);
        return null;
    }

    const fieldErrors = err.problem.errors;
    if (fieldErrors && Object.keys(fieldErrors).length > 0) {
        // Caller renders inline; skip the toast to avoid double-alerting.
        return fieldErrors;
    }

    const msg = err.problem.detail ?? err.problem.title ?? `HTTP ${err.status}`;
    toast.error(`${prefix}${msg}`);
    return null;
}
