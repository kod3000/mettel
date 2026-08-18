# Bruin Inventory Grid — Design Doc

Read + write over 5M inventory rows, three tenants, page-100 grid. Correctness and
honesty over cleverness.

**Why 5M for real.** Estimates and weighted extrapolation would have been accepted
here. I seeded the full 5M anyway, because projected numbers hide the defects that
only exist at size — the 127s scan below was invisible at 100k and was the most
useful thing I found. Due diligence beyond the numbers means seeing it.

**Pagination.** Keyset only. Cursor `v1.<b64payload>.<b64hmac>` carries clientId,
sort key, dir, filter-hash, `(sortValue, id)`; tenant mismatch or changed filter/sort
→ 400. Row-value comparison `(sort_col, id) < ($1,$2)` is mandatory since sort
columns are non-unique. Keyset also means concurrent inserts can't produce the
duplicate rows or silent skips that offset paging causes mid-scroll — the page
boundary is a value, not a position.

**Sub-500ms at 5M.** Six indexes, each leading with `client_id`: 3 btree composites
(created_at; updated_at; status+created_at), 1 unique (client_id, service_number),
2 GIN — (client_id, search_tsv) and (client_id, service_number trigram) via
`btree_gin`, so tenant scope stays inside the index. Search is
`to_tsquery('simple','q:*')` + prefix-anchored `ILIKE 'q%'` for GIN selectivity
(ADR-0002). Connection init sets `plan_cache_mode=force_custom_plan`: Npgsql's
generic plan chose created_at scans touching 3.5M rows on no-match terms (127s).
`reltuples` cached 30s; cap-count skipped when the page fits.

Bench (`bench/live.js` via k6 against https://mettel.exercise.dany.codes over the
public internet — single Mac hosting api + pg-primary + pg-replica + worker + nginx):
cold list p95 **105ms @1 VU, 114ms @10 VU, 238ms @100 VU** (target ≤500ms met across
the sweep). Search p95 **229ms @1 VU, 411ms @10 VU**, 3.7s @100 VU; filtered p95
290ms @1 VU, 494ms @10 VU, 6.8s @100 VU. Plans are correct at the DB layer —
uncontended `EXPLAIN (ANALYZE, BUFFERS)` runs ~400ms per query, and the @100 VU
regression on filtered/search is queue amplification on a shared laptop
(p95/p50 ≈ 7×). Whether dedicated Postgres actually clears 500ms at 100 VU is
untested — 100ms of headroom over uncontended plan time is thin margin, and
`bench/results.md` is honest about that. Per-tenant @100 VU (acme 3.5M / beacon
1.8M / cascade 724K) is within run-to-run noise across the 5× row-count spread
— the composite index seeks directly to the page regardless of tenant size.
Full VU sweep + methodology in `bench/results.md`.

**Protecting the primary writer.** Two Npgsql pools, `IReadRouter` scoped per
request. Empty `X-Min-LSN` → replica; present → cached `pg_last_wal_replay_lsn()`
compare (250ms TTL) → replica, or primary fallback counted as
`bruin_read_primary_fallback_total`. `/health/ready` fails on unreachable replica or
>8MB replication lag (byte-based; timestamp lag lies on idle systems).

**Schema.** Postgres 17, `pg_trgm` + `btree_gin`. UUIDv7 ids double as time-sortable
keyset tiebreakers. `search_tsv` GENERATED ALWAYS … STORED over
product_name/address/notes. Enums are text + CHECK, not PG enum types (altering
those in migrations is painful). `timestamptz` throughout; trigger owns `updated_at`
+ `row_version`. Status transitions (pending→active→disconnected, plus documented
pending→disconnected) are enforced in **`Domain/StatusTransitions.cs`** — the one
table shared by the single-row `WriteHandler` and the bulk `BulkJobRunner`, so
operator and import see the same law — and rejected as ProblemDetails, never a
silent no-op.

**Bulk jobs.** POST persists the file, inserts `bulk_job`, returns 202 in <100ms —
no parsing in the handler. Worker (BackgroundService, same image, `--worker`) claims
via `SELECT … FOR UPDATE SKIP LOCKED` + a session-scoped `pg_try_advisory_lock` held
across the whole chunk loop, so N workers never race on one job and a crashed
worker's lock releases with its connection (no `locked_until` column needed).
5000-row chunks: validate → COPY into `ON COMMIT DROP` staging → `SELECT DISTINCT
ON (client_id, service_number)` collapses within-file duplicates → `INSERT …
ON CONFLICT DO NOTHING RETURNING id` drops rows that collide with existing DB rows.
Losers from both passes are found by join-back on the staging id (winners
survive, everything else is written to `bulk_job_error` with reason
`"duplicate service_number"`) so the errors CSV surfaces every rejected row
regardless of which dedup pass caught it. `processed_rows` checkpoints in the
same transaction as the insert, so crash + restart resumes at the chunk boundary
with an identical final success count (test covered).

**Frontend + contract coherence.** 300ms debounce on search; every in-flight
request carries an **AbortController + query-key ordering guard**, so a slow
early keystroke can never paint over a fast later one. Sort or filter change
resets to page 1 and drops the cursor; scroll appends without touching selection.
The two halves share one contract rather than mirroring each other: types and
enums (`status`, `productCategory`) are **generated from OpenAPI into
`packages/api-types`** via `openapi-typescript`, so a server rename breaks the
build instead of the grid. To keep that generation deterministic the API strips
its `servers` block via a document transformer — otherwise the committed spec
flipped between `localhost:8081` and `127.0.0.1:8081` depending on which host
`curl` was pointed at, and `make verify` was noisy for the wrong reason. The
cursor is opaque to the client — it echoes what the server issued and never
constructs one, so pagination semantics live in exactly one place, and a cursor
whose filter-hash no longer matches is rejected server-side rather than trusted.
Create is the clearest case: the write returns its LSN, the client sends it back
as `X-Min-LSN` on refetch, and the router guarantees the operator reads their
own write even off a replica. Read-your-own-writes is a property of the two ends
agreeing, not of either one alone. To exercise that contract from a second
runtime, the same OpenAPI drives a Blazor WebAssembly twin at
`wasm.mettel.exercise.dany.codes` — feature-identical to the React SPA with a
hand-written client over the generated types, so any breaking rename fails both
builds instead of silently drifting in one. A third consumer,
`packages/mcp-server`, republishes the same surface as ten Model Context
Protocol tools with zod-typed inputs mirroring the OpenAPI enums, so any
MCP-capable agent (Claude Desktop, Cursor, custom SDK loops) can drive the
grid without a browser — and any contract change surfaces there too.

**Multi-tenancy + roles.** Every statement carries `WHERE client_id = @cid`;
the EF Core global filter is defence in depth; Postgres RLS on `inventory` is
built as the third belt (`CREATE ROLE bruin_app NOLOGIN` + a
`current_setting('app.current_client_id')` policy live in the initial
migration), but the app still connects as the superuser `bruin` for now, so
RLS is loaded, not armed. Flipping it on is a connection-string swap plus a
`SET LOCAL app.current_client_id` per request — deferred rather than shipped
so the claim in this doc matches what runs. Cross-tenant reads return 404 with
a ProblemDetails body that leaks nothing — no 403, no FK error.
API keys carry a role (`admin` / `worker` / `reader`) resolved on every request;
a `RequireRole` endpoint filter gates mutations, and `field_policy` names the
per-tenant columns only admins may write (e.g. `notes`). `/me` returns the
tenant, role, and admin-only field list in one shot so the SPA can gate write UI
without a second round-trip. Deletes are soft — `deleted_at IS NOT NULL` rows
stay for audit and re-import; every read filter and the `(client_id,
service_number)` unique index carry a `WHERE deleted_at IS NULL` predicate. A
practical consequence: soft-deleting a row frees its service number for reuse,
which is what operators want when re-running a fixed-up CSV, but does mean
"duplicate" is a live-only concept — historical rows aren't candidates. The
bulk-jobs `ON CONFLICT` clause carries the same predicate so Postgres can infer
the partial index (a plain `ON CONFLICT (client_id, service_number)` raises
42P10 and the worker silently loops; caught once, guarded now by an integration
test).

**What production looks like.** Not this, deliberately. The grid should read a
replica or a purpose-built projection, never the OLTP table — separating the read
and write paths is what keeps operator UX stable while bulk jobs and status churn
hit the primary. The projection arrives over a sync pipeline (logical replication or
CDC into a denormalised read model), which buys eventual consistency; the LSN router
above is already the mechanism that makes that correct rather than merely fast. CSV
work moves to its own deployment — bursty and memory-hungry, with no business
competing against grid reads for one pod's memory. API runs as replicas with node
anti-affinity so losing a node costs capacity, not the service; `/health/ready`
already gates traffic, config is env-driven, workers scale on queue depth.

I'd ship that as a minor version and correct against observed traffic rather than
guess twice. The inputs I'd want first: how operators actually use the grid (which
filters, which sorts, what they do after a search), what constraints the data
carries across its full journey, and the shape of demand — zones, per-sector
language mix, whether load bursts 09:00–17:00 and drops after 18:00 relative to
zone. That drives index choice, cache TTLs, and bulk scheduling far better than a
synthetic bench does.

**What I'd revisit.** (1) Real hardware for the bench — needed to actually test
whether removing the laptop bottleneck clears the 500ms gate at 100 VU, not just
assume it. (2) Configuration sensitivity study with N≥5 runs per point: the
`bench/results.md` sensitivity table shows filtered p95 non-monotonic across
tuning steps (best ever measured was 8CPU/16GB shared_buffers 384MB at 726ms,
shipped config is 2.5× worse), and single 45s runs at 100 VU can't distinguish
signal from noise. Either the shipped 12CPU/20GB tuning is actively wrong or
the runs are too short; both are testable. (3) Full-substring search via an
inverted index over a normalised column, or an external engine. (4)
`saved_view.columns` UI — the show/hide cog on the grid shipped (browser-local),
but wiring it to `saved_view.columns` persistence so a view carries its column
layout across sessions is still open.
(5) Optimistic create — chose refetch-with-LSN above, which makes the router
visibly correct instead of dressing up latency. (6) Render path, once saved views
let operators pull multi-thousand-row sets into the DOM: row virtualization first,
and a WebAssembly render layer only if profiling shows the bottleneck is paint
rather than fetch. At 100 rows a page it isn't, so that stays a hypothesis rather
than a plan.

**Hours.** ~**15hrs** end-to-end. The tooling below compressed the build; most of
that time went to reading its output and chasing the two defects it hid.

**AI tooling.** Claude Code and Kimi generated most backend and frontend
scaffolding, migrations, and tests. I overrode them when they papered over the bench
p95 miss instead of naming the VM constraint, proposed a MATERIALIZED CTE strictly
worse than the prefix-search fix, and left `plan_cache_mode` at default — which
reproduced the 127s scan under load. Every "quick fix" that hid a real defect got
reversed once we had a repro.