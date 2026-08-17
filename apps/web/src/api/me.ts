import { useQuery } from "@tanstack/react-query";
import type { components } from "@bruin/api-types";
import type { Role } from "../tenants.js";

// Wire shape from the OpenAPI spec, retyped with the narrow Role union so
// consumers get the union instead of the API's flat `string`. The API
// enforces the role vocabulary via a CHECK constraint on api_key.role, so
// widening back to `string` is safe as far as the server is concerned.
type WireMe = components["schemas"]["MeResponse"];
export type MeResponse = Omit<WireMe, "role"> & { role: Role };

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
