# Bruin Inventory Grid

Full-stack fun-times for MetTel Bruin Platform. ASP.NET Core 9 minimal API,
Postgres 17 (streaming primary + replica), Vite + React 19 + TypeScript grid
over 5 M rows across 3 tenants.

**Live demo:** https://mettel.exercise.dany.codes/
**Design decisions:** [`design-doc.md`](./design-doc.md) (one page).
**Bench numbers + trajectory:** [`bench/results.md`](./bench/results.md).

## Prerequisites

- macOS or Linux with Docker (Colima on macOS: `colima start --cpu 8
  --memory 12` recommended; 12/20 is the tuning `bench/results.md` reports).
- `.NET SDK 9` (only for `make verify` and the test suite locally; the API
  itself runs in the compose stack).
- Node 20+ (for the frontend + `packages/api-types` codegen).

## Quickstart

```sh
make up       # builds + starts pg-primary, pg-replica, api, worker, web
make seed     # generates 5,000,000 inventory rows across 3 tenants (~90 s)
# → open http://localhost:5173 for the grid
```

- **Grid:** http://localhost:5173 (Vite dev proxy → API at `http://api:8080`).
- **API:** http://localhost:8081 (host-side; the container listens on 8080).
- **OpenAPI:** http://localhost:8081/openapi/v1.json.
- **Health:** http://localhost:8081/health/ready.

## Seeded API keys

Each tenant has a fixed API key — the frontend defaults to Acme and carries
a client picker so a reviewer can swap tenants without editing anything.

| Tenant | Rows (~) | `X-Api-Key` |
|---|---:|---|
| Acme Telecom | 3 500 000 | `pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme` |
| Beacon Networks | 1 250 000 | `pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_beacon` |
| Cascade Communications | 250 000 | `pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_cascade` |

## Common tasks

**Create a row from the grid:** click **+ New** in the header, fill in
serviceNumber / productCategory / productName, submit. Duplicate service
numbers land the server error on the exact field.

**Upload a CSV:** use the "Bulk upload" panel. Try the deliberately-mixed
sample:

```sh
curl -H "X-Api-Key: pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme" \
     -F "file=@seed/sample-upload.csv;type=text/csv" \
     http://localhost:8081/api/v1/bulk-jobs
```

Response is `202` with a `jobId`. The `bruin-worker` container processes
it in ~5 000-row chunks (`SELECT … FOR UPDATE SKIP LOCKED`). Watch progress
in the UI or over SSE:

```sh
curl -N -H "X-Api-Key: pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme" -H "Accept: text/event-stream" \
     http://localhost:8081/api/v1/bulk-jobs/<jobId>/events
```

**Fetch the CSV template:**

```sh
curl -sS -H "X-Api-Key: pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme" \
     http://localhost:8081/api/v1/inventory/csv-template
```

**Run the benchmark:** `make bench` (100 VUs × 45 s × 5 scenarios; writes
`bench/results.md`). Overrides: `VUS=25 DURATION=30s DEEP=100000`.

**Regenerate typed client:** `make verify` (rebuilds API, runs tests,
refreshes `packages/api-types` from the running OpenAPI doc).

## Repo layout

```
apps/api/           ASP.NET Core 9 minimal API + BulkJobRunner (--worker mode)
apps/web/           Vite + React 19 + TypeScript grid (TanStack Table/Query/Virtual)
packages/api-types/ Generated TypeScript types (openapi-typescript)
seed/               Dotnet console app for the 5 M-row generator + sample-upload.csv
bench/              k6 scenarios + results.md
ops/                Postgres init + replica bootstrap + nginx conf for the deploy host
tests/              xUnit + Testcontainers.PostgreSql (targeted, not exhaustive)
```

## Make targets

| Command | What it does |
|---|---|
| `make up` | Build + start the compose stack, wait for `/health/ready` |
| `make down` | Stop containers |
| `make reset` | Down + drop volumes (fresh replica basebackup on next `up`) |
| `make seed` | 5 M rows via `Npgsql binary COPY`. `ROWS=100000` fast path |
| `make bench` | Toggle `BRUIN_BENCH_MODE=1`, run k6, write `bench/results.md` |
| `make verify` | Build, tests, regen OpenAPI + TS types, typecheck |
| `make psql-primary` / `psql-replica` | Shell into either DB |

## Deploy

Same codebase deploys to https://mettel.exercise.dany.codes/. The deploy
host runs the same compose stack (API + worker + PG primary + replica) and
serves the SPA as static files behind nginx.

- **SPA:** `cd apps/web && npm run build`, then rsync `dist/` into the
  web root pointed to by `ops/nginx/mettel.exercise.dany.codes.conf`
  (the `root` directive — set to `/srv/mettel/web` in the template).
- **API + worker:** `docker compose up -d api worker` on the deploy host,
  same as local.
- **nginx:** drop `ops/nginx/mettel.exercise.dany.codes.conf` into
  `sites-available`, wire up Certbot for the domain, reload.
- **Public URL aliases:** the SPA calls `/api/v1/*` directly; external
  callers can also use `/use/v1/api/*` which the nginx conf rewrites to
  the internal path — useful if you want a stable public URL that doesn't
  leak the internal version scheme.

## Known limitations

- **Bench gate misses at 100 VU on a laptop.** Filtered / search p95
  exceed the 500 ms target on the local Colima VM. Query plans are
  correct, the trajectory is documented in `bench/results.md`. On a
  dedicated Postgres host (16 CPU, 32 GB, real disks), the same code
  hits the gate — see the "Why the last few hundred ms are hard" section
  of that file.
- **Search is prefix-anchored.** `q=fib` matches `Fiber`, not
  `Amplifier`. Substring-anywhere search needs an inverted index over a
  normalised column or an external engine.
- **Column show/hide UI is deferred.** Saved views persist a `columns`
  blob and the endpoint accepts it, but the widget is cut for time.
