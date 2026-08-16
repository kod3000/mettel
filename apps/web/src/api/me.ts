import { useQuery } from "@tanstack/react-query";
import type { Role } from "../tenants.js";

export interface MeResponse {
    clientId: string;
    role: Role;
    adminOnlyFields: string[];
}

// Fetches GET /api/v1/me once per apiKey. The AppShell is remounted when
// tenant or role changes (key={tenant:role}), so a fresh key = fresh mount
// = fresh /me fetch; no explicit invalidation needed.
//
// Errors bubble up as `undefined` data — callers should assume the
// permissive "reader" fallback so a network hiccup can't accidentally
// unlock write UI.
export function useMe(apiKey: string) {
    return useQuery<MeResponse, Error>({
        queryKey: ["me", apiKey],
        queryFn: async () => {
            const res = await fetch("/api/v1/me", {
                headers: { "X-Api-Key": apiKey, "Accept": "application/json" },
            });
            if (!res.ok) throw new Error(`/me returned ${res.status}`);
            return res.json() as Promise<MeResponse>;
        },
        staleTime: 60_000,
        retry: 1,
    });
}
