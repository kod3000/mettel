import { useQueryClient } from "@tanstack/react-query";
import { useRef, useState } from "react";

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

interface Props {
    // Passed from App so bulk upload uses the same key as everything else in
    // the tree — no fallbacks, no reaching into the ApiClient's internals.
    apiKey: string;
}

export function BulkUploadPanel({ apiKey }: Props) {
    const qc = useQueryClient();
    const fileRef = useRef<HTMLInputElement>(null);
    const [job, setJob] = useState<JobSnapshot | null>(null);
    const [phase, setPhase] = useState<"idle" | "uploading" | "streaming" | "polling" | "done" | "error">("idle");
    const [error, setError] = useState<string | null>(null);
    const [rate, setRate] = useState<RateWindow | null>(null);

    async function upload() {
        const file = fileRef.current?.files?.[0];
        if (!file) return;
        setError(null);
        setPhase("uploading");
        setJob(null);
        setRate(null);
        try {
            const form = new FormData();
            form.append("file", file);
            const uploadStart = Date.now();
            const res = await fetch("/api/v1/bulk-jobs", {
                method: "POST",
                headers: { "X-Api-Key": apiKey },
                body: form,
            });
            if (!res.ok) throw new Error(await res.text());
            const body = await res.json() as { jobId: string; status: string };
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
            setError((e as Error).message ?? "upload failed");
            setPhase("error");
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
            <input
                ref={fileRef}
                type="file"
                accept=".csv,text/csv"
                data-testid="bulk-file"
                className="text-xs file:mr-2 file:rounded-md file:border-0 file:bg-slate-100 file:px-2 file:py-1 file:text-xs file:font-medium file:text-slate-700 hover:file:bg-slate-200"
            />
            <button
                type="button"
                onClick={upload}
                disabled={phase === "uploading" || phase === "streaming" || phase === "polling"}
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
            {job && rate && <ProgressBar job={job} rate={rate} phase={phase} error={error} />}
        </div>
    );
}

function ProgressBar({ job, rate, phase, error }: {
    job: JobSnapshot; rate: RateWindow; phase: string; error: string | null;
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

    return (
        <div className="ml-auto flex items-center gap-3 text-xs text-slate-600">
            <span className="truncate max-w-[220px]">
                {job.fileName} <span className="text-slate-400">({phase})</span>
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
                <a
                    href={job.errorSampleUrl}
                    download
                    className="text-indigo-600 hover:text-indigo-700 underline underline-offset-2"
                >
                    error CSV
                </a>
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
