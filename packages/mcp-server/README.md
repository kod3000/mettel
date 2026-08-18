# @bruin/mcp-server

Model Context Protocol server for the Bruin Inventory Grid API. Exposes the
inventory + bulk-jobs surface as MCP tools so any MCP-capable agent host —
Claude Desktop, Cursor, custom SDK clients, Anthropic API tool-use loops —
can drive the grid without a browser.

Model-agnostic. Follows the MCP spec verbatim: no host-specific wording, no
prompt injection, no hidden state.

## Install

```sh
cd packages/mcp-server
npm install
npm run build
```

The compiled entry point is `dist/server.js`. It ships as a `bin` under the
name `bruin-mcp`, so `npx @bruin/mcp-server` also works once published.

## Configuration

Two environment variables, both required:

| Variable | Example | Purpose |
|---|---|---|
| `BRUIN_API_BASE_URL` | `https://mettel.exercise.dany.codes` | Root URL of the Bruin API. No trailing slash needed. |
| `BRUIN_API_KEY` | `pickle-Pepper-…-PEPPERS_acme` | Any valid tenant key. Role (admin / worker / reader) is derived by the server from the key. |

Seeded demo keys are documented in the repo root README. Any tenant admin
key + its `_worker` / `_reader` derivatives are all valid — the server just
passes the key through as `X-Api-Key`.

## Wire to Claude Desktop

Add to `~/Library/Application Support/Claude/claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "bruin": {
      "command": "node",
      "args": ["/absolute/path/to/mt-challenge/packages/mcp-server/dist/server.js"],
      "env": {
        "BRUIN_API_BASE_URL": "https://mettel.exercise.dany.codes",
        "BRUIN_API_KEY": "pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme"
      }
    }
  }
}
```

Restart Claude Desktop; the `bruin` server appears in the tool picker.

## Wire to any MCP client

The server speaks **stdio JSON-RPC**. Any client that spawns a subprocess
and pipes MCP frames over stdio works — Anthropic's TypeScript / Python
SDKs, Cursor, custom Agent SDK loops, etc. There is no HTTP+SSE transport
here on purpose: stdio is simpler to secure (no listener, no CORS) and
matches every documented agent host.

## Tools

Ten tools total. Descriptions in each tool's schema tell an agent when to
reach for it; the summary here is for humans.

| Tool | Read/Write | Purpose |
|---|---|---|
| `me` | R | Tenant id, role, admin-only fields. Call first. |
| `inventory_search` | R | Keyset-paginated search with filters + sort. |
| `inventory_get` | R | Fetch one row by id. |
| `inventory_create` | W | POST /inventory. Admin/worker only. |
| `inventory_update` | W | PATCH arbitrary fields (with `rowVersion`). Admin-only fields per policy. |
| `inventory_change_status` | W | PATCH /status with FSM enforcement. |
| `inventory_delete` | D | Soft-delete. Admin only. |
| `bulk_upload_csv` | W | Upload CSV; returns `jobId` for async processing. |
| `bulk_job_status` | R | Poll a bulk job's status. |
| `bulk_job_errors` | R | Fetch per-row errors after a terminal job status. |

## Recommended workflow for agents

The server also ships this as its `instructions` string, so agents that
surface server instructions see it without you copy-pasting.

1. **Call `me` first.** Cheap; returns your tenant id, role, and the
   per-field policy (`adminOnlyFields`). Every downstream decision about
   writes should reference this — a worker attempting to write `notes`
   (an admin-only field by default) gets a 403 with a per-field error.

2. **Narrow before you scan.** `inventory_search` runs against a table
   with millions of rows. Always pass at least one filter (`status`,
   `productCategory`, `q`, `state`). If searching text, use `fields=`
   to narrow the columns being matched — the default broad search is
   fastest, but a targeted `fields=["serviceNumber"]` is more precise.

3. **Pass cursors verbatim, never construct them.** The `nextCursor` in a
   list response is HMAC-signed and tenant-scoped. Send it back exactly
   as received. Change any filter mid-scroll and the cursor becomes
   stale (`400 cursor-stale`) — start a new search instead.

4. **Include `rowVersion` on every write.** Both `inventory_update` and
   `inventory_change_status` require the `rowVersion` from your most
   recent read. A stale value returns `409 concurrency-conflict` — re-read
   the row and retry.

5. **For bulk imports: upload, poll, then inspect.**
   - `bulk_upload_csv` returns immediately with `{jobId, status: "queued"}`.
   - Poll `bulk_job_status` with backoff (500ms → 2s → 5s → 5s…) until
     the status is one of: `completed`, `completedWithErrors`, `failed`.
   - If `failedRows > 0`, call `bulk_job_errors` for per-row detail
     (`rowNumber`, `serviceNumber`, `reason`, `rawLine`).

6. **Read error `slug` and `status`, not the message string.** Tool errors
   come back as structured JSON: `{ error, status, slug, message,
   fieldErrors }`. `slug` is stable across message wording changes; the
   full slug vocabulary is `validation-failed`, `cursor-invalid`,
   `cursor-stale`, `invalid-status-transition`, `duplicate-service-number`,
   `concurrency-conflict`, `not-found`, `unsupported-media-type`,
   `payload-too-large`, `unauthorized`, `forbidden`.

## Verifying without a client

You can drive the server directly with `printf` + `node` to sanity-check
tool wiring outside a real agent host:

```sh
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0.0.0"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"me","arguments":{}}}' \
  | BRUIN_API_BASE_URL=https://mettel.exercise.dany.codes \
    BRUIN_API_KEY=pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme \
    node dist/server.js 2>/dev/null
```

## Design notes

- **Stateless.** Every tool call is a pass-through to the API — no
  in-memory caching, no cross-call state. Agents that need session state
  should hold it themselves.
- **One key per server instance.** The `X-Api-Key` header is set from
  `BRUIN_API_KEY` at startup; no per-call override is exposed on the tool
  surface, so an agent can't leak / rotate credentials through a prompt.
  Run multiple server instances if you need multiple tenants.
- **Structured errors.** `ProblemDetails` responses are parsed into
  `{error, status, slug, message, fieldErrors}` so agents can switch on
  the slug rather than string-match the human message.
- **Enum'd inputs.** `status`, `productCategory`, `sort`, `dir`, and
  `fields` are typed as literal unions in the input schema so agents get
  compile-time-visible enums, not free-form strings. Values mirror the
  same OpenAPI enum patch that shipped in `apps/api/Program.cs`.

## Adding a tool

1. Create a file in `src/tools/` exporting an object with `name`, `title`,
   `description`, `inputSchema` (zod), and `run(args, cfg)`.
2. Add it to the `TOOLS` array in `src/server.ts`.
3. `npm run build`.

Keep descriptions action-oriented: they're what the agent reads to decide
when to reach for the tool. Prefer specificity ("Fetch a single row by its
UUID") over vagueness ("Get inventory data").
