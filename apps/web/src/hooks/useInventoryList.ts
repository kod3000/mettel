// Paged inventory reads via TanStack Query's useInfiniteQuery.
//
// Key requirements from the build plan (Phase 8):
//   1. Query key encodes the FULL filter+sort tuple so fast typing races are
//      cache-slot-correct — a stale response can never land in the wrong
//      slot and paint over the "new" query's rows.
//   2. In-flight requests aborted on supersede — TanStack Query does this
//      for us as long as the queryFn honours the `signal`.
//   3. Sort/filter changes reset to page 1 — encoded in the key: any change
//      to `params` yields a new key, which is a fresh infinite-query
//      instance. Old pages are dropped on the floor by GC.

import { useInfiniteQuery } from "@tanstack/react-query";
import { useApi } from "../api/context.js";
import { listInventory, listQueryKey, type ListParams, type ListResponse } from "../api/inventory.js";

export function useInventoryList(params: ListParams) {
    const client = useApi();
    return useInfiniteQuery<ListResponse, Error>({
        queryKey: listQueryKey(params),
        initialPageParam: undefined as string | undefined,
        queryFn: async ({ pageParam, signal }) => {
            return await listInventory(
                client,
                { ...params, cursor: pageParam as string | undefined },
                { signal });
        },
        getNextPageParam: (last) => last.hasMore ? last.nextCursor ?? undefined : undefined,
        // Small stale time so identical requests don't refetch during a
        // scroll burst; the API contract already tolerates approximate
        // freshness for reads (X-Min-LSN handles the read-your-own-writes
        // case).
        staleTime: 5_000,
    });
}
