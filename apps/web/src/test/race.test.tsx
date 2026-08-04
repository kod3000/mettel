// Phase 8 race gate: fire 10 rapid keystrokes with staggered mock latencies
// and assert only the FINAL query's rows render.
//
// The trick: response for "abc" (typed early) is programmed with a large
// delay, while the response for the final "abcdefghij" comes back fast.
// If the client cared only about arrival order the "abc" result could win.
// With a full-key query cache + AbortSignal supersede, the late "abc"
// response is either aborted or lands in a cache slot the UI no longer
// reads from — the final value survives.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { ApiContext } from "../api/context.js";
import type { ApiClient } from "../api/client.js";
import { InventoryGrid } from "../components/InventoryGrid.js";
import { Filters } from "../components/Filters.js";
import type { InventoryRow, ListParams, ListResponse } from "../api/inventory.js";

function makeRow(id: string, sn: string): InventoryRow {
    return {
        id, serviceNumber: sn,
        productCategory: "voice", productName: `P-${sn}`, status: "active",
        city: "New York", state: "NY", address: "1 Test St",
        assignee: null, notes: null,
        createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
        rowVersion: 1,
    };
}

interface FakeSlot { delayMs: number; response: ListResponse; }

function fakeClient(program: Map<string, FakeSlot>, calls: { q: string; aborted: boolean }[]): ApiClient {
    const request = async <T,>(_path: string, opts?: {
        query?: Record<string, unknown>;
        signal?: AbortSignal;
    }): Promise<T> => {
        const q = (opts?.query?.q as string | undefined) ?? "";
        const slot = program.get(q) ?? { delayMs: 5, response: emptyResponse() };
        const record = { q, aborted: false };
        calls.push(record);
        return await new Promise<T>((resolve, reject) => {
            const id = setTimeout(() => resolve(slot.response as unknown as T), slot.delayMs);
            opts?.signal?.addEventListener("abort", () => {
                clearTimeout(id);
                record.aborted = true;
                reject(new DOMException("aborted", "AbortError"));
            });
        });
    };
    return {
        request,
        get: (p, o) => request(p, o),
        post: (p, b, o) => request(p, { ...o, method: "POST", body: b } as unknown as Parameters<typeof request>[1]),
        patch: (p, b, o) => request(p, { ...o, method: "PATCH", body: b } as unknown as Parameters<typeof request>[1]),
    };
}

function emptyResponse(): ListResponse {
    return {
        rows: [], nextCursor: null, hasMore: false,
        totalEstimate: { value: 0, kind: "approximate" },
        filteredCount: { value: 0, kind: "exact" }, tookMs: 1,
    };
}

function withRow(id: string, sn: string): ListResponse {
    return {
        rows: [makeRow(id, sn)],
        nextCursor: null, hasMore: false,
        totalEstimate: { value: 1, kind: "approximate" },
        filteredCount: { value: 1, kind: "exact" }, tookMs: 1,
    };
}

function Harness({ initial }: { initial: ListParams }) {
    const [params, setParams] = useState<ListParams>(initial);
    return (
        <>
            <Filters value={params} onChange={setParams} />
            <InventoryGrid params={params} onParamsChange={setParams} />
        </>
    );
}

describe("grid race conditions", () => {
    it("only the final query's rows are visible after 10 rapid keystrokes", async () => {
        const program = new Map<string, FakeSlot>([
            ["a",   { delayMs: 600, response: withRow("row-a",   "111-000-0001") }],
            ["ab",  { delayMs: 550, response: withRow("row-ab",  "111-000-0002") }],
            ["abc", { delayMs: 500, response: withRow("row-abc", "111-000-0003") }],
            ["abcdefghij", { delayMs: 30, response: withRow("row-final", "111-000-9999") }],
        ]);
        const calls: { q: string; aborted: boolean }[] = [];
        const client = fakeClient(program, calls);
        const qc = new QueryClient({ defaultOptions: { queries: { retry: 0, staleTime: 0 } } });

        render(
            <ApiContext.Provider value={client}>
                <QueryClientProvider client={qc}>
                    <Harness initial={{ pageSize: 100 }} />
                </QueryClientProvider>
            </ApiContext.Provider>);

        const user = userEvent.setup();
        const input = screen.getByTestId("search-input") as HTMLInputElement;
        await user.type(input, "abcdefghij");

        await waitFor(() => {
            const rows = screen.queryAllByTestId("grid-row").map((n) => n.textContent ?? "");
            expect(rows.some((t) => t.includes("111-000-9999"))).toBe(true);
        }, { timeout: 3000 });

        // Wait a little more so the LATE responses have time to potentially
        // land and clobber; only if they did NOT we're allowed to pass.
        await new Promise((r) => setTimeout(r, 800));

        const rendered = screen.getAllByTestId("grid-row").map((n) => n.textContent ?? "");
        expect(rendered.some((t) => t.includes("111-000-9999"))).toBe(true);
        expect(rendered.some((t) => t.includes("111-000-0001"))).toBe(false);
        expect(rendered.some((t) => t.includes("111-000-0002"))).toBe(false);
        expect(rendered.some((t) => t.includes("111-000-0003"))).toBe(false);
    }, 10_000);
});
