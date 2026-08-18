#!/usr/bin/env node
// Bruin Inventory Grid — MCP server.
//
// Transport: stdio. That's what Claude Desktop, Cursor, and most MCP CLIs
// expect. HTTP+SSE transport is intentionally out of scope for the demo —
// stdio is simpler to secure (no listener, no CORS) and fits every
// documented agent host.
//
// Config: two env vars, both required.
//   BRUIN_API_BASE_URL  e.g. https://mettel.exercise.dany.codes
//   BRUIN_API_KEY       any valid tenant key (see the README for the roles)
//
// The server has no state of its own — every tool is a stateless
// pass-through to the API. Auth is a single key applied to every call;
// no per-request key override is exposed on the tool surface so agents
// can't accidentally leak/rotate credentials via prompts.

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
    CallToolRequestSchema,
    ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { zodToJsonSchema } from "zod-to-json-schema";
import type { ZodTypeAny } from "zod";
import type { BruinConfig } from "./client.js";
import { ApiError } from "./client.js";
import { meTool } from "./tools/me.js";
import {
    inventorySearchTool,
    inventoryGetTool,
    inventoryCreateTool,
    inventoryUpdateTool,
    inventoryChangeStatusTool,
    inventoryDeleteTool,
} from "./tools/inventory.js";
import {
    bulkUploadCsvTool,
    bulkJobStatusTool,
    bulkJobErrorsTool,
} from "./tools/bulk-jobs.js";

interface Tool {
    name: string;
    title: string;
    description: string;
    inputSchema: ZodTypeAny;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    run(args: any, cfg: BruinConfig): Promise<unknown>;
}

const TOOLS: Tool[] = [
    meTool,
    inventorySearchTool,
    inventoryGetTool,
    inventoryCreateTool,
    inventoryUpdateTool,
    inventoryChangeStatusTool,
    inventoryDeleteTool,
    bulkUploadCsvTool,
    bulkJobStatusTool,
    bulkJobErrorsTool,
];

function loadConfig(): BruinConfig {
    const baseUrl = process.env.BRUIN_API_BASE_URL?.trim();
    const apiKey = process.env.BRUIN_API_KEY?.trim();
    if (!baseUrl) throw new Error("BRUIN_API_BASE_URL is required (e.g. https://mettel.exercise.dany.codes)");
    if (!apiKey) throw new Error("BRUIN_API_KEY is required (see README for the seeded tenant keys)");
    return { baseUrl, apiKey };
}

async function main(): Promise<void> {
    const cfg = loadConfig();

    const server = new Server(
        { name: "bruin-mcp", version: "0.1.0" },
        {
            capabilities: { tools: {} },
            // Server instructions shown to the model when tools are listed.
            // Keep short and action-oriented; agent hosts surface this
            // verbatim, so no model-specific wording.
            instructions: [
                "Bruin Inventory Grid — read + write inventory rows and drive bulk CSV imports.",
                "",
                "Recommended workflow:",
                "1. Call `me` first to learn the tenant, role, and admin-only field list.",
                "2. Use `inventory_search` with narrow filters (status, productCategory, fields=) — the table has millions of rows.",
                "3. For writes, include `rowVersion` from the most recent read for optimistic concurrency.",
                "4. For bulk imports: `bulk_upload_csv` returns a jobId immediately. Poll `bulk_job_status` with backoff (500ms → 2s → 5s) until a terminal status. If failedRows > 0, call `bulk_job_errors` to inspect per-row failures.",
                "",
                "Cursors are opaque, HMAC-signed, and tenant-scoped — pass them back verbatim and never construct one yourself. Any filter change invalidates a cursor with slug `cursor-stale`.",
            ].join("\n"),
        },
    );

    // ---- List tools ------------------------------------------------------
    server.setRequestHandler(ListToolsRequestSchema, async () => ({
        tools: TOOLS.map(t => ({
            name: t.name,
            title: t.title,
            description: t.description,
            inputSchema: zodToJsonSchema(t.inputSchema, { $refStrategy: "none" }) as Record<string, unknown>,
        })),
    }));

    // ---- Call tool -------------------------------------------------------
    server.setRequestHandler(CallToolRequestSchema, async (req) => {
        const tool = TOOLS.find(t => t.name === req.params.name);
        if (!tool) {
            return {
                isError: true,
                content: [{ type: "text" as const, text: `Unknown tool: ${req.params.name}` }],
            };
        }

        // Validate the arguments through the tool's zod schema so a malformed
        // call gives the agent a clear error instead of a 4xx from the API.
        const parsed = tool.inputSchema.safeParse(req.params.arguments ?? {});
        if (!parsed.success) {
            return {
                isError: true,
                content: [{ type: "text" as const, text: `Invalid arguments: ${parsed.error.message}` }],
            };
        }

        try {
            const result = await tool.run(parsed.data, cfg);
            return {
                content: [{
                    type: "text" as const,
                    text: JSON.stringify(result, null, 2),
                }],
            };
        } catch (err) {
            if (err instanceof ApiError) {
                // Structured error surface: give the agent the slug + status
                // (machine-readable) plus the human message, so it can decide
                // whether to retry, adjust arguments, or bail.
                const payload = {
                    error: true,
                    status: err.status,
                    slug: err.slug,
                    message: err.message,
                    fieldErrors: err.fieldErrors,
                };
                return {
                    isError: true,
                    content: [{ type: "text" as const, text: JSON.stringify(payload, null, 2) }],
                };
            }
            const message = err instanceof Error ? err.message : String(err);
            return {
                isError: true,
                content: [{ type: "text" as const, text: `Tool "${tool.name}" failed: ${message}` }],
            };
        }
    });

    // Stdio transport is the standard MCP host contract. Nothing else
    // should write to stdout in this process — the MCP framing lives there.
    // Anything diagnostic must go to stderr.
    const transport = new StdioServerTransport();
    await server.connect(transport);
    process.stderr.write(`bruin-mcp: ready (baseUrl=${cfg.baseUrl}, ${TOOLS.length} tools)\n`);
}

main().catch(err => {
    process.stderr.write(`bruin-mcp: fatal: ${err instanceof Error ? err.stack ?? err.message : String(err)}\n`);
    process.exit(1);
});
