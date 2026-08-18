import { useQueryClient } from "@tanstack/react-query";
import { useRef, useState } from "react";
import { toast } from "./Toaster.js";

interface JobSnapshot {
    jobId: string;
    status: string;
    fileName: string;
    totalRows: number;
    processedRows: number;
    succeededRows: number;
    failedRows: number;
    errorSampleUrl: string;
}

// Throughput samples so the UI can render a rolling rows/sec without
// spiking on short frames. We keep a small ring of the last few SSE frames.
interface RateWindow {
    startedAtMs: number;   // wall-clock ms when the job started (client-side)
    lastProcessed: number;
    lastAtMs: number;
    instantRowsPerSec: number;
    averageRowsPerSec: number;
    uploadStartMs: number; // client kicked off the POST
    uploadBytes: number;
}

// Live upload state — populated by XHR's upload.onprogress before the
// server responds with a jobId. Lets us render a progress bar during the
// POST itself, which for a 50 MB file over a residential uplink can be
// many seconds of otherwise-blank UI. `fetch()` doesn't expose upload
// progress; XHR does.
interface UploadProgress {
    fileName: string;
    loaded: number;   // bytes sent so far
    total: number;    // total bytes (0 = unknown / not-computable)
    startedAtMs: number;
}

interface Props {
    // Passed from App so bulk upload uses the same key as everything else in
    // the tree — no fallbacks, no reaching into the ApiClient's internals.
    apiKey: string;
    // Reader keys 403 on POST /bulk-jobs; hide the upload UI entirely to
    // avoid a click-then-fail interaction. Read-only tenants still see the
    // "CSV template" / "Sample 500k CSV" download links.
    canWrite: boolean;
}

export function BulkUploadPanel({ apiKey, canWrite }: Props) {
    const qc = useQueryClient();
    const fileRef = useRef<HTMLInputElement>(null);
    const [job, setJob] = useState<JobSnapshot | null>(null);
    const [phase, setPhase] = useState<"idle" | "uploading" | "streaming" | "polling" | "done" | "error">("idle");
    const [error, setError] = useState<string | null>(null);
    const [rate, setRate] = useState<RateWindow | null>(null);
    const [uploadProgress, setUploadProgress] = useState<UploadProgress | null>(null);

    async function upload() {
        const file = fileRef.current?.files?.[0];
        if (!file) return;
        setError(null);
        setPhase("uploading");
        setJob(null);
        setRate(null);
        setUploadProgress({
            fileName: file.name,
            loaded: 0,
            total: file.size,
            startedAtMs: Date.now(),
        });
        try {
            // XHR (not fetch) because we need upload.onprogress events —
            // fetch's Request body doesn't emit progress even with a
            // ReadableStream on Chrome without HTTP/2 duplex which is
            // gated behind an experimental flag.
            const uploadStart = Date.now();
            const body = await postWithProgress(
                "/api/v1/bulk-jobs",
                { "X-Api-Key": apiKey },
                file,
                (loaded, total) => setUploadProgress((p) => p ? { ...p, loaded, total } : p),
            );
            setUploadProgress(null);
            const initial: JobSnapshot = {
                jobId: body.jobId, status: body.status, fileName: file.name,
                totalRows: 0, processedRows: 0, succeededRows: 0, failedRows: 0,
                errorSampleUrl: `/api/v1/bulk-jobs/${body.jobId}/errors`,
            };
            setJob(initial);
            setRate({
                startedAtMs: Date.now(),
                lastProcessed: 0,
                lastAtMs: Date.now(),
                instantRowsPerSec: 0,
                averageRowsPerSec: 0,
                uploadStartMs: uploadStart,
                uploadBytes: file.size,
            });
            await streamProgress(body.jobId, initial);
        } catch (e: unknown) {
            setUploadProgress(null);
            const msg = (e as Error).message ?? "upload failed";
            setError(msg);
            setPhase("error");
            // Pre-accept failures render nothing in the panel (the inline
            // <ProgressBar> only mounts when `job` is set), so surface the
            // server's ProblemDetails through the toaster instead.
            toast.error(`Upload failed — ${msg}`);
        }
    }

    async function streamProgress(jobId: string, seed: JobSnapshot) {
        setPhase("streaming");
        try {
            const res = await fetch(`/api/v1/bulk-jobs/${jobId}/events`, {
                headers: { "X-Api-Key": apiKey, "Accept": "text/event-stream" },
            });
            if (!res.body) throw new Error("no body");

            const reader = res.body.getReader();
            const decoder = new TextDecoder("utf-8");
            let buffered = "";
            let lastSnap = seed;
            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                buffered += decoder.decode(value, { stream: true });
                const events = buffered.split("\n\n");
                buffered = events.pop() ?? "";
                for (const raw of events) {
                    const lines = raw.split("\n");
                    const evName = lines.find((l) => l.startsWith("event: "))?.slice(7).trim() ?? "message";
                    const data = lines.filter((l) => l.startsWith("data: ")).map((l) => l.slice(6)).join("\n");
                    if (!data) continue;
                    try {
                        const snap = JSON.parse(data) as JobSnapshot;
                        setJob(snap);
                        setRate((prev) => updateRate(prev, snap));
                        lastSnap = snap;
                        if (evName === "done") {
                            setPhase("done");
                            qc.invalidateQueries({ queryKey: ["inventory", "list"] });
                            await maybeToastJobLevelFailure(snap, apiKey);
                            return;
                        }
                    } catch { /* ignore malformed frame */ }
                }
            }
            await pollProgress(jobId, lastSnap);
        } catch {
            await pollProgress(jobId, seed);
        }
    }

    async function pollProgress(jobId: string, seed: JobSnapshot) {
        setPhase("polling");
        let snap = seed;
        for (let i = 0; i < 240; i++) {
            try {
                const res = await fetch(`/api/v1/bulk-jobs/${jobId}`, {
                    headers: { "X-Api-Key": apiKey },
                });
                if (res.ok) {
                    snap = await res.json() as JobSnapshot;
                    setJob(snap);
                    setRate((prev) => updateRate(prev, snap));
                    if (snap.status === "completed" || snap.status === "completedWithErrors" || snap.status === "failed") {
                        setPhase("done");
                        qc.invalidateQueries({ queryKey: ["inventory", "list"] });
                        await maybeToastJobLevelFailure(snap, apiKey);
                        return;
                    }
                }
            } catch { /* keep polling */ }
            await new Promise((r) => setTimeout(r, 500));
        }
        setPhase("error");
        setError("polling timeout — check job status manually");
    }

    return (
        <div className="flex flex-wrap items-center gap-3 border-b border-slate-200 bg-white px-4 py-2 text-xs">
            <span className="text-slate-500">Bulk upload</span>
            {/* Reader sees the same controls, disabled — reviewer letter:
                the permission model should teach itself. */}
            <input
                ref={fileRef}
                type="file"
                accept=".csv,text/csv"
                disabled={!canWrite}
                title={canWrite ? undefined : "Requires admin or worker role"}
                data-testid="bulk-file"
                className="text-xs file:mr-2 file:rounded-md file:border-0 file:bg-slate-100 file:px-2 file:py-1 file:text-xs file:font-medium file:text-slate-700 hover:file:bg-slate-200 disabled:opacity-50 disabled:cursor-not-allowed"
            />
            <button
                type="button"
                onClick={upload}
                disabled={!canWrite || phase === "uploading" || phase === "streaming" || phase === "polling"}
                title={canWrite ? undefined : "Requires admin or worker role"}
                className="rounded-md bg-slate-800 px-2.5 py-1 text-xs font-medium text-white shadow-sm hover:bg-slate-700 disabled:bg-slate-300 disabled:cursor-not-allowed"
            >
                Upload
            </button>
            <a
                href="#"
                onClick={(e) => { e.preventDefault(); downloadCsv(apiKey, "/api/v1/inventory/csv-template", "inventory-template.csv"); }}
                className="text-indigo-600 hover:text-indigo-700 underline underline-offset-2"
            >
                CSV template
            </a>
            <a
                href="#"
                onClick={(e) => { e.preventDefault(); downloadCsv(apiKey, "/api/v1/inventory/csv-sample?rows=500000", "bruin-sample-500k.csv"); }}
                title="~50 MB stream of realistic rows for load-testing the bulk upload path"
                className="text-indigo-600 hover:text-indigo-700 underline underline-offset-2"
            >
                Sample 500k CSV
            </a>
            {uploadProgress && !job && <UploadProgressBar up={uploadProgress} />}
            {job && rate && <ProgressBar job={job} rate={rate} phase={phase} error={error} apiKey={apiKey} />}
        </div>
    );
}

// Rendered while the POST body is streaming to the server (before the 202
// response comes back with a jobId). Once we have a jobId, the richer
// ProgressBar takes over.
function UploadProgressBar({ up }: { up: UploadProgress }) {
    const pct = up.total > 0 ? Math.min(100, Math.round((up.loaded / up.total) * 100)) : 0;
    const elapsedSec = Math.max(0.001, (Date.now() - up.startedAtMs) / 1000);
    const mbps = (up.loaded / 1024 / 1024) / elapsedSec;
    return (
        <div className="ml-auto flex items-center gap-3 text-xs text-slate-600" data-testid="upload-progress">
            <span className="truncate max-w-[220px]">
                {up.fileName} <span className="text-slate-400">(uploading)</span>
            </span>
            <div className="flex items-center gap-2">
                <div className="w-40 h-2 bg-slate-100 rounded-full overflow-hidden">
                    <div
                        className="h-full bg-sky-500 transition-[width] duration-150"
                        style={{ width: `${pct}%` }}
                    />
                </div>
                <span className="tabular-nums text-slate-500 w-9 text-right">{pct}%</span>
            </div>
            <span className="tabular-nums text-slate-500">
                {(up.loaded / 1024 / 1024).toFixed(1)} / {(up.total / 1024 / 1024).toFixed(1)} MB
            </span>
            {mbps > 0.05 && (
                <span className="tabular-nums text-slate-400" title="Wire throughput of the POST body">
                    {mbps.toFixed(1)} MB/s
                </span>
            )}
        </div>
    );
}

// Fetch equivalent that surfaces upload progress via XHR events. Returns
// the parsed 202 body ({jobId, status}) or throws with the server's
// response text on non-2xx.
function postWithProgress(
    url: string,
    headers: Record<string, string>,
    file: File,
    onProgress: (loaded: number, total: number) => void,
): Promise<{ jobId: string; status: string }> {
    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open("POST", url, true);
        for (const [k, v] of Object.entries(headers)) xhr.setRequestHeader(k, v);
        xhr.upload.onprogress = (e) => {
            // lengthComputable is false when Content-Length isn't known;
            // for a File-based multipart it's always known. Guard anyway.
            if (e.lengthComputable) onProgress(e.loaded, e.total);
        };
        xhr.onload = () => {
            if (xhr.status >= 200 && xhr.status < 300) {
                try { resolve(JSON.parse(xhr.responseText)); }
                catch { reject(new Error("invalid response JSON")); }
            } else {
                reject(new Error(problemMessage(xhr.responseText, xhr.status)));
            }
        };
        xhr.onerror = () => reject(new Error("network error during upload"));
        xhr.onabort = () => reject(new Error("upload aborted"));

        // Send the file inside a multipart form (matches what the server's
        // ReadFormAsync expects — see BulkJobEndpoints.AcceptUploadAsync).
        const form = new FormData();
        form.append("file", file);
        xhr.send(form);
    });
}

function ProgressBar({ job, rate, phase, error, apiKey }: {
    job: JobSnapshot; rate: RateWindow; phase: string; error: string | null; apiKey: string;
}) {
    const total = Math.max(job.totalRows, job.processedRows, 1);
    const pct = Math.min(100, Math.round((job.processedRows / total) * 100));
    const barColor =
        job.status === "failed" ? "bg-rose-500"
        : job.status === "completedWithErrors" ? "bg-amber-500"
        : job.status === "completed" ? "bg-emerald-500"
        : "bg-indigo-500";

    const elapsedSec = (Date.now() - rate.startedAtMs) / 1000;
    const terminal = job.status === "completed" || job.status === "completedWithErrors" || job.status === "failed";
    const remaining = Math.max(0, total - job.processedRows);
    const etaSec = terminal ? 0
        : rate.averageRowsPerSec > 0 ? Math.round(remaining / rate.averageRowsPerSec)
        : 0;
    const uploadThroughputMBs = rate.uploadBytes > 0
        ? (rate.uploadBytes / 1024 / 1024) / Math.max(0.01, (rate.startedAtMs - rate.uploadStartMs) / 1000)
        : 0;

    // Show the server's terminal status ("failed", "completedWithErrors",
    // "completed") in the phase label once terminal — otherwise the label
    // reads "done" for every ending, indistinguishable from success.
    const label = terminal ? job.status : phase;

    return (
        <div className="ml-auto flex items-center gap-3 text-xs text-slate-600">
            <span className="truncate max-w-[220px]">
                {job.fileName} <span className="text-slate-400">({label})</span>
            </span>
            <div className="flex items-center gap-2">
                <div className="w-40 h-2 bg-slate-100 rounded-full overflow-hidden">
                    <div
                        className={`h-full ${barColor} transition-[width] duration-200`}
                        style={{ width: `${pct}%` }}
                    />
                </div>
                <span className="tabular-nums text-slate-500 w-9 text-right">{pct}%</span>
            </div>
            <span className="tabular-nums">
                <span className="text-emerald-700">{job.succeededRows.toLocaleString()}</span>
                <span className="text-slate-400"> ok / </span>
                <span className="text-rose-700">{job.failedRows.toLocaleString()}</span>
                <span className="text-slate-400"> err</span>
            </span>
            <span className="tabular-nums text-slate-500" title="Rolling rows/sec (last snapshot)">
                {formatRate(rate.instantRowsPerSec)} r/s
            </span>
            {!terminal && rate.averageRowsPerSec > 0 && (
                <span className="tabular-nums text-slate-500" title="ETA at current average rate">
                    ETA {formatDuration(etaSec)}
                </span>
            )}
            {terminal && (
                <span className="tabular-nums text-slate-500" title="Total wall-clock elapsed">
                    in {formatDuration(elapsedSec)}
                </span>
            )}
            {uploadThroughputMBs > 0.05 && (
                <span className="tabular-nums text-slate-400" title="Upload throughput (POST body only)">
                    upload {uploadThroughputMBs.toFixed(1)} MB/s
                </span>
            )}
            {job.status === "completedWithErrors" && (
                <ErrorCsvLink jobId={job.jobId} apiKey={apiKey} />
            )}
            {error && <span className="text-rose-700">{error}</span>}
        </div>
    );
}

// Rolling-average with instant-frame smoothing. The instant rate is what
// the user "feels" — the average is what drives ETA. Both are computed
// against the client-side clock so nothing depends on server-side StartedAt.
function updateRate(prev: RateWindow | null, snap: JobSnapshot): RateWindow | null {
    if (!prev) return prev;
    const now = Date.now();
    const dSec = Math.max(0.001, (now - prev.lastAtMs) / 1000);
    const dRows = Math.max(0, snap.processedRows - prev.lastProcessed);
    const instant = dRows / dSec;
    // Client-side average over the whole run — more stable for ETA.
    const avgSec = Math.max(0.001, (now - prev.startedAtMs) / 1000);
    const average = snap.processedRows / avgSec;
    return {
        ...prev,
        lastProcessed: snap.processedRows,
        lastAtMs: now,
        instantRowsPerSec: instant,
        averageRowsPerSec: average,
    };
}

function formatRate(v: number): string {
    if (v >= 10_000) return `${(v / 1000).toFixed(1)}k`;
    if (v >= 1_000)  return `${(v / 1000).toFixed(2)}k`;
    return v < 1 ? "0" : Math.round(v).toString();
}

function formatDuration(sec: number): string {
    if (!isFinite(sec) || sec < 0) return "—";
    if (sec < 60) return `${Math.round(sec)}s`;
    if (sec < 3600) {
        const m = Math.floor(sec / 60);
        const s = Math.round(sec % 60);
        return `${m}m ${s}s`;
    }
    const h = Math.floor(sec / 3600);
    const m = Math.round((sec % 3600) / 60);
    return `${h}h ${m}m`;
}

// A plain <a href download> can't set X-Api-Key (→ 401) or Accept: text/csv
// (→ server returns JSON, which the browser saves inside a .csv file).
// This wraps the download in a fetch that sets both headers, then triggers
// the save via a temporary blob URL — same pattern as the CSV template /
// sample downloads elsewhere on the page.
function ErrorCsvLink({ jobId, apiKey }: { jobId: string; apiKey: string }) {
    async function onClick(e: React.MouseEvent) {
        e.preventDefault();
        const res = await fetch(`/api/v1/bulk-jobs/${jobId}/errors`, {
            headers: { "X-Api-Key": apiKey, "Accept": "text/csv" },
        });
        if (!res.ok) {
            toast.error(`Could not download error CSV — HTTP ${res.status}`);
            return;
        }
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url; a.download = `bruin-errors-${jobId}.csv`;
        a.click();
        URL.revokeObjectURL(url);
    }
    return (
        <a
            href="#"
            onClick={onClick}
            className="text-indigo-600 hover:text-indigo-700 underline underline-offset-2"
        >
            error CSV
        </a>
    );
}

// Terminal-status → toaster.
//   status=failed              → job-level reason (row_number=0). Written by
//                                BulkJobRunner.MarkFailedAsync when the file
//                                is malformed at the whole-file level (bad
//                                header, empty file, missing file on disk).
//   status=completedWithErrors → preview the first few row errors. The full
//                                list is still one click away via the
//                                "error CSV" link so we cap the toast at 3
//                                rows to keep it readable.
// Silent on status=completed.
async function maybeToastJobLevelFailure(snap: JobSnapshot, apiKey: string): Promise<void> {
    if (snap.status !== "failed" && snap.status !== "completedWithErrors") return;
    try {
        const res = await fetch(`/api/v1/bulk-jobs/${snap.jobId}/errors`, {
            headers: { "X-Api-Key": apiKey, "Accept": "application/json" },
        });
        if (!res.ok) {
            toast.error(`Job ${snap.status} — see /api/v1/bulk-jobs/${snap.jobId}/errors`);
            return;
        }
        const body = await res.json() as {
            errors?: Array<{ rowNumber: number; serviceNumber?: string | null; reason: string }>;
        };
        const all = body.errors ?? [];

        if (snap.status === "failed") {
            const jobLevel = all.filter((e) => e.rowNumber === 0);
            const reason = jobLevel.length > 0
                ? jobLevel.map((e) => e.reason).join("; ")
                : "Job failed with no reason recorded.";
            toast.error(`Job failed — ${reason}`);
            return;
        }

        // completedWithErrors
        const rowErrors = all.filter((e) => e.rowNumber > 0);
        if (rowErrors.length === 0) return;
        const preview = rowErrors.slice(0, 3).map((e) => {
            const sn = e.serviceNumber ? ` (${e.serviceNumber})` : "";
            return `row ${e.rowNumber}${sn}: ${e.reason}`;
        }).join("\n");
        // Prefer the server's authoritative failedRows count over
        // rowErrors.length — the /errors endpoint caps at 500 entries.
        const total = snap.failedRows > 0 ? snap.failedRows : rowErrors.length;
        const remaining = total - Math.min(3, rowErrors.length);
        const suffix = remaining > 0 ? `\n…and ${remaining} more (see error CSV)` : "";
        toast.error(`${total} row${total === 1 ? "" : "s"} failed:\n${preview}${suffix}`, 15000);
    } catch {
        toast.error("Job errors — could not fetch details.");
    }
}

// The API's 4xx responses are RFC 7807 ProblemDetails JSON — parse and
// pull out the human-facing text. Falls back to the raw body / HTTP code
// so we never silently swallow a message we don't understand.
function problemMessage(body: string, status: number): string {
    if (body) {
        try {
            const p = JSON.parse(body) as { detail?: string; title?: string };
            const text = p.detail ?? p.title;
            if (text) return text;
        } catch { /* not JSON — fall through */ }
        return body;
    }
    return `HTTP ${status}`;
}

async function downloadCsv(apiKey: string, path: string, filename: string) {
    const res = await fetch(path, { headers: { "X-Api-Key": apiKey } });
    if (!res.ok) return;
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url; a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
}
