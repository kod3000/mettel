import { useEffect, useState } from "react";

// Minimal imperative toast API. Modules that need to surface a
// user-visible message import `toast` and call it from anywhere —
// no context wiring, no props threading. A single <Toaster /> mounted
// at the app root subscribes to the store and renders the stack.

export type ToastKind = "error" | "info";
export interface Toast {
    id: number;
    message: string;
    kind: ToastKind;
}

const listeners = new Set<(t: Toast[]) => void>();
let items: Toast[] = [];
let seq = 0;

function emit() {
    for (const l of listeners) l(items);
}

function push(kind: ToastKind, message: string, ttlMs: number): number {
    const id = ++seq;
    items = [...items, { id, message, kind }];
    emit();
    if (ttlMs > 0) window.setTimeout(() => dismiss(id), ttlMs);
    return id;
}

function dismiss(id: number) {
    items = items.filter((t) => t.id !== id);
    emit();
}

export const toast = {
    error: (message: string, ttlMs = 8000) => push("error", message, ttlMs),
    info:  (message: string, ttlMs = 4000) => push("info",  message, ttlMs),
    dismiss,
};

function useToasts(): Toast[] {
    const [state, setState] = useState<Toast[]>(items);
    useEffect(() => {
        listeners.add(setState);
        return () => { listeners.delete(setState); };
    }, []);
    return state;
}

export function Toaster() {
    const toasts = useToasts();
    if (toasts.length === 0) return null;
    return (
        <div
            role="region"
            aria-label="Notifications"
            className="fixed top-4 right-4 z-50 flex flex-col gap-2 max-w-md pointer-events-none"
        >
            {toasts.map((t) => (
                <div
                    key={t.id}
                    role={t.kind === "error" ? "alert" : "status"}
                    data-testid={`toast-${t.kind}`}
                    className={`pointer-events-auto flex items-start gap-3 rounded-md border px-3 py-2 text-sm shadow-lg ${
                        t.kind === "error"
                            ? "border-rose-300 bg-rose-50 text-rose-900"
                            : "border-slate-300 bg-white text-slate-900"
                    }`}
                >
                    <span className="flex-1 whitespace-pre-wrap break-words leading-snug">{t.message}</span>
                    <button
                        type="button"
                        onClick={() => dismiss(t.id)}
                        aria-label="Dismiss"
                        className={`shrink-0 rounded px-1 text-xs font-medium ${
                            t.kind === "error"
                                ? "text-rose-700 hover:bg-rose-100"
                                : "text-slate-500 hover:bg-slate-100"
                        }`}
                    >
                        ✕
                    </button>
                </div>
            ))}
        </div>
    );
}
