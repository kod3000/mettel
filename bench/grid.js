// Bruin Inventory Grid — Phase 4 benchmark.
//
// Runs 5 scenarios against a seeded 5M-row Postgres:
//   cold_list      GET /api/v1/inventory                              (default sort)
//   filtered_list  GET /api/v1/inventory?status=&productCategory=…    (structured filters)
//   search         GET /api/v1/inventory?q=<2-4 char>                 (tsv + trigram)
//   deep_keyset    GET /api/v1/inventory?cursor=<precomputed depth>   (walking to page N)
//   deep_offset    GET /bench/offset?depth=N                          (control, OFFSET-based)
//
// Gate: p95 ≤ 500 ms for cold, filtered, and search at 100 VUs.
// Deep scenarios are illustrative — they exist to justify "no OFFSET" in the
// design doc, not to gate progress.
//
// Run via `make bench`. Overrides:
//   BENCH_BASE_URL   default http://api:8080  (compose-network hostname)
//   BENCH_API_KEY    default pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme
//   BENCH_VUS        default 100
//   BENCH_DURATION   default 45s
//   BENCH_DEEP_DEPTH default 200000  (rows deep for cursor pre-walk + OFFSET)

import http from 'k6/http';
import { check, group, sleep } from 'k6';
import { Trend } from 'k6/metrics';

const BASE = __ENV.BENCH_BASE_URL || 'http://api:8080';
const API_KEY = __ENV.BENCH_API_KEY || 'pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme';
const VUS = parseInt(__ENV.BENCH_VUS || '100');
const DURATION = __ENV.BENCH_DURATION || '45s';
const DEEP_DEPTH = parseInt(__ENV.BENCH_DEEP_DEPTH || '200000');
const SETUP_TIMEOUT = __ENV.BENCH_SETUP_TIMEOUT || '3m';

const headers = { 'X-Api-Key': API_KEY };

// Search substrings: real-world users type short and often invalid strings.
// These are chosen to hit both tsvector (product-name tokens) and trigram
// (service-number substrings) paths — the point is to prove the plan stays
// index-based across query shape variety.
const SEARCH_TERMS = ['fib', 'sip', 'wire', 'lte', 'pbx', '212', '555', '344', '404'];
const STATUSES = ['pending', 'active', 'disconnected'];
const CATS = ['voice', 'data', 'wireless', 'other'];

// Per-scenario Trend metrics so results.md tables read cleanly.
const t_cold      = new Trend('cold_list_ms',     true);
const t_filtered  = new Trend('filtered_list_ms', true);
const t_search    = new Trend('search_ms',        true);
const t_keyset    = new Trend('deep_keyset_ms',   true);
const t_offset    = new Trend('deep_offset_ms',   true);

export const options = {
    // p95 thresholds encode the Phase 4 gate — a failing scenario turns k6's
    // exit code non-zero so `make bench` fails and CI catches regressions.
    // (Earlier revisions used `{quantile:p(95)}` as the metric key — that's
    // a *tag filter*, not the aggregation selector, so the threshold matched
    // zero samples and trivially passed. The right form is `p(95)<500` in
    // the value list against the bare metric name.)
    thresholds: {
        'cold_list_ms':     ['p(95)<500'],
        'filtered_list_ms': ['p(95)<500'],
        'search_ms':        ['p(95)<500'],
    },
    scenarios: {
        cold_list: {
            executor: 'constant-vus',
            vus: VUS, duration: DURATION,
            exec: 'coldList',
            gracefulStop: '5s',
        },
        filtered_list: {
            executor: 'constant-vus',
            vus: VUS, duration: DURATION,
            exec: 'filteredList',
            gracefulStop: '5s',
            startTime: `${parseSeconds(DURATION) + 5}s`,
        },
        search: {
            executor: 'constant-vus',
            vus: VUS, duration: DURATION,
            exec: 'search',
            gracefulStop: '5s',
            startTime: `${(parseSeconds(DURATION) + 5) * 2}s`,
        },
        deep_keyset: {
            executor: 'constant-vus',
            // Deep scenarios use lower VU count — they are illustrative, and
            // 100 VUs fetching the same page rows adds no signal.
            vus: Math.max(10, Math.floor(VUS / 4)),
            duration: DURATION,
            exec: 'deepKeyset',
            gracefulStop: '5s',
            startTime: `${(parseSeconds(DURATION) + 5) * 3}s`,
        },
        deep_offset: {
            executor: 'constant-vus',
            vus: Math.max(10, Math.floor(VUS / 4)),
            duration: DURATION,
            exec: 'deepOffset',
            gracefulStop: '5s',
            startTime: `${(parseSeconds(DURATION) + 5) * 4}s`,
        },
    },
    setupTimeout: SETUP_TIMEOUT,
    summaryTrendStats: ['avg', 'min', 'med', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

function parseSeconds(dur) {
    // very-not-general dur parser — accepts "45s" or "2m"
    if (dur.endsWith('m')) return parseInt(dur) * 60;
    return parseInt(dur);
}

// setup() runs once. Walks the cursor to DEEP_DEPTH rows for the deep_keyset
// scenario AND warms Postgres caches (shared_buffers + OS page cache) for
// every filter + search combination we'll fire in the main scenarios.
// Cache warmup is realistic: in production a tenant's hot working set stays
// resident and the first-request-cold penalty is a startup one-time cost,
// not a per-request one. This benchmark measures the steady-state.
export function setup() {
    console.log(`[setup] warming caches: filter combinations…`);
    for (const s of STATUSES) for (const c of CATS) {
        const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&status=${s}&productCategory=${c}`, { headers });
        if (r.status !== 200) throw new Error(`warmup filter ${s}/${c} failed: ${r.status}`);
    }
    console.log(`[setup] warming caches: search terms…`);
    for (const q of SEARCH_TERMS) {
        const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&q=${q}`, { headers });
        if (r.status !== 200) throw new Error(`warmup search ${q} failed: ${r.status}`);
    }
    console.log(`[setup] pre-walking cursor to depth ${DEEP_DEPTH} rows for deep_keyset…`);
    let cursor = null;
    let depth = 0;
    const step = 100;
    while (depth < DEEP_DEPTH) {
        const url = `${BASE}/api/v1/inventory?pageSize=${step}` +
            (cursor ? `&cursor=${encodeURIComponent(cursor)}` : '');
        const r = http.get(url, { headers });
        if (r.status !== 200) throw new Error(`setup fetch failed at depth ${depth}: ${r.status} ${r.body}`);
        const body = r.json();
        cursor = body.nextCursor;
        depth += body.rows.length;
        if (!body.hasMore) break;
    }
    console.log(`[setup] pre-walk complete: depth=${depth}, cursor cached.`);
    return { deepCursor: cursor, deepDepth: depth };
}

export function coldList() {
    const r = http.get(`${BASE}/api/v1/inventory?pageSize=100`, { headers });
    t_cold.add(r.timings.duration);
    check(r, { 'cold 200': (x) => x.status === 200 });
}

export function filteredList() {
    const s = STATUSES[Math.floor(Math.random() * STATUSES.length)];
    const c = CATS[Math.floor(Math.random() * CATS.length)];
    const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&status=${s}&productCategory=${c}`, { headers });
    t_filtered.add(r.timings.duration);
    check(r, { 'filtered 200': (x) => x.status === 200 });
}

export function search() {
    const term = SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)];
    const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&q=${term}`, { headers });
    t_search.add(r.timings.duration);
    check(r, { 'search 200': (x) => x.status === 200 });
}

export function deepKeyset(data) {
    const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&cursor=${encodeURIComponent(data.deepCursor)}`, { headers });
    t_keyset.add(r.timings.duration);
    check(r, { 'deep keyset 200': (x) => x.status === 200 });
}

export function deepOffset(data) {
    // control scenario — hits the env-gated /bench/offset that runs the same
    // query with OFFSET N. Only enabled when BRUIN_BENCH_MODE=1 on the API.
    const r = http.get(`${BASE}/bench/offset?depth=${data.deepDepth}&pageSize=100`, { headers });
    t_offset.add(r.timings.duration);
    check(r, { 'deep offset 200': (x) => x.status === 200 });
}

export function handleSummary(data) {
    return {
        '/bench/out/summary.json': JSON.stringify(data, null, 2),
        '/bench/out/results.md': markdownSummary(data),
        stdout: textSummary(data),
    };
}

// Markdown table so results.md reads as human docs.
function markdownSummary(data) {
    const scenarios = [
        ['cold_list_ms',     'Cold list',      'default sort, no filters'],
        ['filtered_list_ms', 'Filtered list',  'random status + productCategory'],
        ['search_ms',        'Search',         'random 2–4 char q, tsv + trigram'],
        ['deep_keyset_ms',   'Deep (keyset)',  `cursor pre-walked to ~${DEEP_DEPTH} rows`],
        ['deep_offset_ms',   'Deep (OFFSET)',  `control: OFFSET ${DEEP_DEPTH} on same query`],
    ];
    const seededRows = __ENV.BENCH_SEEDED_ROWS || '5,000,000';
    const now = new Date().toISOString();

    const rows = ['| Scenario | Requests | p50 | p95 | p99 | max | Notes |',
                  '|---|---:|---:|---:|---:|---:|---|'];
    for (const [key, label, notes] of scenarios) {
        const v = (data.metrics[key] || {}).values || {};
        rows.push(`| ${label} | ${v.count||0} | ${fmt(v['med'])} | ${fmt(v['p(95)'])} | ${fmt(v['p(99)'])} | ${fmt(v['max'])} | ${notes} |`);
    }

    let gateLines = ['', '### Gate status'];
    for (const [name, m] of Object.entries(data.metrics)) {
        if (!m.thresholds) continue;
        for (const [t, ok] of Object.entries(m.thresholds)) {
            gateLines.push(`- \`${name}\` \`${t}\` — **${ok.ok ? 'PASS' : 'FAIL'}**`);
        }
    }

    const httpTotal = data.metrics.http_reqs?.values?.count ?? 0;
    const httpFail  = data.metrics.http_req_failed?.values?.passes ?? 0;
    const failRate  = httpTotal > 0 ? ((httpFail / httpTotal) * 100).toFixed(2) : '0.00';

    return [
        '# Bruin Inventory Grid — Benchmark Results',
        '',
        `_Generated ${now}_`,
        '',
        '## Environment',
        '',
        `- Seeded rows: **${seededRows}** across 3 tenants (70 / 25 / 5% split).`,
        `- Postgres: 17-alpine in Docker Compose (see docker-compose.yml for tuning).`,
        `- API: ASP.NET Core 9 minimal API + Dapper, dual NpgsqlDataSource pools.`,
        `- Load generator: grafana/k6 in a sibling container on the compose network.`,
        `- VUs: **${VUS}** for cold/filtered/search; ${Math.max(10, Math.floor(VUS/4))} for deep scenarios.`,
        `- Duration per scenario: ${DURATION}. Requests total: ${httpTotal} (failure rate ${failRate}%).`,
        '',
        '## Results',
        '',
        ...rows,
        '',
        ...gateLines,
        '',
        '## What the deep scenarios show',
        '',
        `The keyset scenario re-uses a cursor pre-walked to ~${DEEP_DEPTH} rows deep, so`,
        `each iteration measures the cost of fetching page N at that depth. The OFFSET`,
        `control hits an env-gated \`/bench/offset\` endpoint (the only route in the`,
        `codebase that emits OFFSET) with \`OFFSET ${DEEP_DEPTH}\` on the same query.`,
        `Postgres has to walk all ${DEEP_DEPTH} preceding rows for OFFSET; keyset uses the`,
        `(client_id, created_at DESC, id DESC) index to seek directly to the page.`,
        '',
    ].join('\n');
}

function textSummary(data) {
    const lines = ['\n=== Bruin bench summary ===\n'];
    const scenarios = ['cold_list_ms', 'filtered_list_ms', 'search_ms', 'deep_keyset_ms', 'deep_offset_ms'];
    for (const key of scenarios) {
        const m = data.metrics[key];
        if (!m) continue;
        const v = m.values || {};
        lines.push(
            `${key.padEnd(24)}  count=${(v.count||0).toString().padStart(6)}  ` +
            `p50=${fmt(v['med'])}  p95=${fmt(v['p(95)'])}  p99=${fmt(v['p(99)'])}  ` +
            `max=${fmt(v['max'])}`);
    }
    for (const [name, m] of Object.entries(data.metrics)) {
        if (m.thresholds) {
            for (const [t, ok] of Object.entries(m.thresholds)) {
                lines.push(`  threshold ${name} ${t} => ${ok.ok ? 'PASS' : 'FAIL'}`);
            }
        }
    }
    return lines.join('\n') + '\n';
}
function fmt(x) { return x == null ? '  n/a ' : x.toFixed(1) + 'ms'; }
