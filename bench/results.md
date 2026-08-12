# Bruin Inventory Grid — Benchmark Results

_Generated 2026-08-11. Test bed: live deployment at
https://mettel.exercise.dany.codes._

## Environment

- **Test bed:** live deployment. Single Mac running the
  Docker Compose stack (`bruin-api`, `bruin-worker`, `bruin-pg-primary`,
  `bruin-pg-replica`) inside Colima, behind Homebrew nginx with Let's
  Encrypt.
- **Load generator:** `grafana/k6:latest` in Docker on a separate machine,
  hitting the live URL over the public internet (residential uplink;
  single-request warm baseline ~30–130 ms for a 100-row page — RTT
  dominates the low end).
- **Rows seeded:** ~5,998,540 total across 3 tenants, unevenly distributed
  (Acme ~3.5 M, Beacon ~1.8 M, Cascade ~724 K per the tenant-scoped
  `totalEstimate`; sums match the whole-table reltuples).
- **Script:** [`bench/live.js`](./live.js) — three graded scenarios
  (cold_list, filtered_list, search). No `deep_offset` / `deep_keyset` —
  those need `BRUIN_BENCH_MODE=1` which prod doesn't run.
- **Duration:** 30 s per scenario, sequential. Warm-cache setup: 12
  filter combos + 9 search terms fetched once before the run.

## VU sweep — p95 latency on Acme (3.5 M rows)

Each cell is one 30 s scenario at the given VU count. Target ≤ 500 ms.

| Scenario         |  @1 VU |  @10 VU |  @100 VU |
|------------------|-------:|--------:|---------:|
| Cold list        |    105 |     114 |      238 |
| Filtered list    |    290 |     494 |    6 769 |
| Search           |    229 |     411 |    3 692 |

Cold list clears the 500 ms gate at every VU count tested (top end 238 ms
= 52 % under target). Filtered + search meet target at 1 and 10 VU
(filtered @10 VU 1 % under — marginal but a pass) and blow through at
100 VU on the shared laptop test bed.

## Per-tenant @ 100 VU — does tenant size matter?

| Tenant   | Rows       | Cold p95 | Filtered p95 | Search p95 |
|----------|-----------:|---------:|-------------:|-----------:|
| Acme     | ~3 500 348 | 238 ms   | 6 769 ms     | 3 692 ms   |
| Beacon   | ~1 773 768 | 261 ms   | 7 165 ms     | 4 477 ms   |
| Cascade  | ~  724 424 | 230 ms   | 8 325 ms     | 3 405 ms   |

At 100 VU the bottleneck is CPU saturation on the shared laptop, not
per-tenant row count — the three tenants land within run-to-run variance
of each other despite a 5× difference in row count. Cold list runs the
same `(client_id, created_at DESC, id DESC)` index scan and returns 100
rows regardless of tenant size, which is exactly the shape the index was
designed for.

## Per-scenario detail

### Acme @ 1 VU

| Scenario      |    p50 |    p95 |    p99 |    max |
|---------------|-------:|-------:|-------:|-------:|
| Cold list     |  26 ms | 105 ms | 126 ms | 396 ms |
| Filtered list |  77 ms | 290 ms | 330 ms | 450 ms |
| Search        | 133 ms | 229 ms | 273 ms | 351 ms |

_1 435 requests, 0 failures._

### Acme @ 10 VU

| Scenario      |    p50 |    p95 |    p99 |    max |
|---------------|-------:|-------:|-------:|-------:|
| Cold list     |  23 ms | 114 ms | 151 ms | 242 ms |
| Filtered list | 134 ms | 494 ms | 683 ms | 889 ms |
| Search        | 233 ms | 411 ms | 524 ms | 753 ms |

_10 375 requests, 0 failures._

### Acme @ 100 VU

| Scenario      |     p50 |     p95 |     p99 |     max |
|---------------|--------:|--------:|--------:|--------:|
| Cold list     |  102 ms |  238 ms |  309 ms |    356 ms |
| Filtered list |  952 ms | 6 769 ms | 9 154 ms | 14 371 ms |
| Search        | 2 099 ms | 3 692 ms | 4 695 ms |  5 675 ms |

_22 264 requests, 10 failed (0.04 %)._

### Beacon @ 100 VU

| Scenario      |     p50 |     p95 |     p99 |     max |
|---------------|--------:|--------:|--------:|--------:|
| Cold list     |  111 ms |  261 ms |  325 ms |    409 ms |
| Filtered list | 2 533 ms | 7 165 ms | 8 992 ms | 10 299 ms |
| Search        | 1 442 ms | 4 477 ms | 5 571 ms |  7 424 ms |

_21 117 requests, 7 failed (0.03 %)._

### Cascade @ 100 VU

| Scenario      |     p50 |     p95 |     p99 |     max |
|---------------|--------:|--------:|--------:|--------:|
| Cold list     |  107 ms |  230 ms |  296 ms |    380 ms |
| Filtered list | 1 289 ms | 8 325 ms | 11 543 ms | 13 571 ms |
| Search        | 1 613 ms | 3 405 ms |  4 443 ms |  5 290 ms |

_22 808 requests, 7 failed (0.03 %)._

## Gate status (challenge target: p95 ≤ 500 ms)

- Cold list: **PASS** at 1 / 10 / 100 VU across all three tenants.
- Filtered list: **PASS** at 1 & 10 VU (Acme). **FAIL** at 100 VU on the
  shared laptop (all three tenants: 6.8 s / 7.2 s / 8.3 s).
- Search: **PASS** at 1 & 10 VU (Acme). **FAIL** at 100 VU on the shared
  laptop (all three tenants: 3.7 s / 4.5 s / 3.4 s).

The plans themselves are not the bottleneck: uncontended
`EXPLAIN (ANALYZE, BUFFERS)` runs the graded filtered + search queries
in ~400 ms per query against the same ~6 M-row dataset. Whether a
dedicated Postgres box actually clears the 500 ms p95 gate at 100 VU
is untested — 100 ms of headroom above uncontended plan time is thin
margin for queue amplification, and we have no measurement from real
hardware to back the claim up.

## Why 100 VU regresses on live

The live host is a laptop running the entire Compose stack + nginx +
the usual daily-driver processes. At 100 concurrent complex filter /
search queries against a multi-million-row table, Postgres wants
~10–15 CPU-sec/s of work. Sharing that with 100 nginx keepalive slots
+ API request handling + kernel network stack on ~10 vCPU puts
utilization past ~70 %, where p95 starts amplifying superlinearly
relative to p50. Classic queue behaviour; `p95 / p50 ≈ 7×` for
filtered at 100 VU says the same thing.

- Cold list (cheap index scan, small result) stays under target because
  it doesn't cost enough per-request to saturate.
- Filtered list (btree composite on `(client_id, status, created_at)`)
  costs more, and the extra work per request eats CPU faster than cold.
- Search (GIN tsvector + trigram) is the most CPU-heavy path per request.

What we know: the code, plans, and indexes are the same across
scenarios; the shared laptop is demonstrably CPU-saturated at 100 VU
(p95/p50 ≈ 7× is textbook queue amplification, not a plan defect).
What we don't know: whether removing that specific bottleneck on a
dedicated Postgres box is *sufficient* to clear the 500 ms gate at
100 VU, or whether further work (materialized read model, external
search index) is required. Uncontended plan time already sits at ~400
ms; queue amplification under real concurrency has to fit in ~100 ms
of remaining budget, which we haven't measured.

## Configuration sensitivity (not a monotonic trajectory)

Seven point-measurements from tuning attempts against localhost, single
45 s runs at 100 VU × ~6 M-row dataset. Presenting these as a
"trajectory" would overstate the story — the numbers do not move
monotonically:

| Change | Filtered p95 | Search p95 |
|---|---:|---:|
| Baseline (Colima 2 CPU / 2 GB, 256 MB shared_buffers) | 5 206 ms | 6 213 ms |
| Colima **8 CPU / 16 GB**, PG shared_buffers 384 MB | **726 ms** | 2 125 ms |
| Colima 12 CPU / 20 GB, PG 4 GB / 12 GB tuning | 2 838 ms | **1 981 ms** |
| + `to_tsquery('q:*')` prefix search + custom-plan enforcement | 2 633 ms | 3 272 ms |
| + Npgsql pool 200, PG `max_connections=300`, tenant-scoped estimator | 4 805 ms | 3 212 ms |
| + Skip capped-count when result fits one page | 2 229 ms | 1 493 ms |
| + PG `max_parallel_workers_per_gather=0` (shipped config) | 1 827 ms | 1 499 ms |

Best filtered p95 ever measured was **row 2 (726 ms** at 8 CPU / 16 GB,
384 MB shared_buffers). The shipped config is **2.5× worse** than that.
Search regresses at the prefix-search step (1 981 → 3 272 ms) before
recovering. Two live hypotheses, neither disproved:

1. **The 12 CPU / 20 GB Colima + 4 GB / 12 GB PG tuning is actively
   wrong for this workload.** Would need to re-run row 2's config with
   every subsequent code change applied to test.
2. **Single 45 s runs at 100 VU are too noisy to distinguish these.**
   filtered p95 varying 726 → 4 805 across "improvements" while search
   drifts 1 499–3 272 suggests substantial run-to-run variance. Would
   need N ≥ 5 runs per config, reported as median + IQR, before drawing
   ordering conclusions.

The shipped p95 (1 827 ms filtered / 1 499 ms search on Colima
localhost) is the last run against the shipped config — it may or may
not be the best attainable with this code. Reproducing takes ~10 min
per config; the sensitivity study is called out under "What I'd
revisit" in `design-doc.md`.

## Reproducing

Local Colima (`make bench`, gates enabled):

    make bench VUS=100 DURATION=45s

Live deployment (this run — swap `<tenant>` for `acme`, `beacon`, or
`cascade`):

    docker run --rm -i -v $(pwd)/bench:/scripts \
      -v $(pwd)/bench/out-post-fix:/bench/out \
      -e BENCH_BASE_URL=https://mettel.exercise.dany.codes \
      -e BENCH_API_KEY=pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_<tenant> \
      -e BENCH_VUS=100 -e BENCH_DURATION=30s \
      grafana/k6:latest run --summary-export=/bench/out/<tenant>-100vu.json \
      /scripts/live.js

Per-run JSON summaries: `bench/out-post-fix/{acme,beacon,cascade}-*.json`.
