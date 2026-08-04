// Injects the API client into the React tree so hooks + components can
// consume it without prop drilling and tests can substitute a mock.

import { createContext, useContext } from "react";
import type { ApiClient } from "./client.js";

export const ApiContext = createContext<ApiClient | null>(null);

export function useApi(): ApiClient {
    const c = useContext(ApiContext);
    if (!c) throw new Error("ApiContext missing — wrap the tree in <ApiContext.Provider>");
    return c;
}
