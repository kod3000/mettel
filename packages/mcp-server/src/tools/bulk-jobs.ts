import { z } from "zod";
import type { BruinConfig } from "../client.js";
import { request } from "../client.js";

// Bulk-jobs tools. Recommended workflow for agents:
//   1. bulk_upload_csv → returns { jobId, status: "queued" }
//   2. Poll bulk_job_status with backoff (500ms → 2s → 5s) until
//      status ∈ { completed, completedWithErrors, failed }
//   3. If failedRows > 0, call bulk_job_errors to see which rows and why
//
// The upload path streams the file into a job; the worker processes it in
// 5000-row chunks under an advisory lock (safe with N workers, crash-safe
// resume). Return-to-the-agent should always be the jobId — do not block
// the upload call waiting for terminal status.

export const bulkUploadCsvTool = {
    name: "bulk_upload_csv",
    title: "Upload a CSV for bulk import (async)",
    description:
        "Upload an inventory CSV. Returns immediately with { jobId, status: 'queued' } — the worker processes asynchronously. Header row required: serviceNumber,productCategory,productName,status (extra columns city,state,address,assignee,notes are optional). Max file size is 200 MB (returns 413 above). Follow up with bulk_job_status to poll progress and bulk_job_errors if any rows fail.",
    inputSchema: z.object({
        filename: z.string().describe("Name for the uploaded file (used in error reports)."),
        content: z.string().describe(
            "Full CSV contents as a UTF-8 string. For large files (>10 MB), consider staging the file on disk and using the raw HTTP endpoint directly.",
        ),
    }),
    async run(args: { filename: string; content: string }, cfg: BruinConfig) {
        const bytes = new TextEncoder().encode(args.content);
        return request(cfg, {
            method: "POST",
            path: "/api/v1/bulk-jobs",
            file: { filename: args.filename, contentType: "text/csv", data: bytes },
        });
    },
};

export const bulkJobStatusTool = {
    name: "bulk_job_status",
    title: "Poll a bulk job's progress",
    description:
        "Return current job state. Terminal statuses are: completed (all rows ok), completedWithErrors (some rows failed but the job ran to end), failed (job-level failure like a bad CSV header). Non-terminal: queued, processing. Poll with backoff — 500ms initially, capped at 5s, until a terminal status is reached.",
    inputSchema: z.object({
        jobId: z.string().uuid().describe("Job id returned by bulk_upload_csv."),
    }),
    async run(args: { jobId: string }, cfg: BruinConfig) {
        return request(cfg, { path: `/api/v1/bulk-jobs/${encodeURIComponent(args.jobId)}` });
    },
};

export const bulkJobErrorsTool = {
    name: "bulk_job_errors",
    title: "Fetch per-row errors for a completed bulk job",
    description:
        "Return the per-row error list for a job (parse errors + duplicates + invalid statuses). Only meaningful once the job has reached a terminal status with failedRows > 0. Each entry carries rowNumber (1-based, includes header), serviceNumber (if parseable), reason, and rawLine so agents can show a user the exact input that failed.",
    inputSchema: z.object({
        jobId: z.string().uuid(),
    }),
    async run(args: { jobId: string }, cfg: BruinConfig) {
        return request(cfg, { path: `/api/v1/bulk-jobs/${encodeURIComponent(args.jobId)}/errors` });
    },
};
