// Live-deployment k6 script — variant of grid.js trimmed for hitting
// https://mettel.exercise.dany.codes over the public internet:
//
//   - No `deep_offset` scenario (that route is gated by BRUIN_BENCH_MODE
//     which prod doesn't run — would 404).
//   - No deep-cursor pre-walk in setup (setup time was dominated by 200k
//     rows fetched over the WAN; not what this bench measures).
//   - Only cold_list / filtered_list / search — the three thresholded
//     scenarios from the original bench.
//
// Env:
//   BENCH_BASE_URL   default https://mettel.exercise.dany.codes
//   BENCH_API_KEY    default pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme
//   BENCH_VUS        default 10
//   BENCH_DURATION   default 30s

import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const BASE = __ENV.BENCH_BASE_URL || 'https://mettel.exercise.dany.codes';
const API_KEY = __ENV.BENCH_API_KEY || 'pickle-Pepper-PETTER-piPEr-picKEd-PEPPERS_acme';
const VUS = parseInt(__ENV.BENCH_VUS || '10');
const DURATION = __ENV.BENCH_DURATION || '30s';

const headers = { 'X-Api-Key': API_KEY };
const SEARCH_TERMS = ['fib', 'sip', 'wire', 'lte', 'pbx', '212', '555', '344', '404'];
const STATUSES = ['pending', 'active', 'disconnected'];
const CATS = ['voice', 'data', 'wireless', 'other'];

const t_cold     = new Trend('cold_list_ms',     true);
const t_filtered = new Trend('filtered_list_ms', true);
const t_search   = new Trend('search_ms',        true);

export const options = {
    scenarios: {
        cold_list: {
            executor: 'constant-vus', vus: VUS, duration: DURATION,
            exec: 'coldList', gracefulStop: '5s',
        },
        filtered_list: {
            executor: 'constant-vus', vus: VUS, duration: DURATION,
            exec: 'filteredList', gracefulStop: '5s',
            startTime: `${parseSeconds(DURATION) + 5}s`,
        },
        search: {
            executor: 'constant-vus', vus: VUS, duration: DURATION,
            exec: 'search', gracefulStop: '5s',
            startTime: `${(parseSeconds(DURATION) + 5) * 2}s`,
        },
    },
    summaryTrendStats: ['avg', 'min', 'med', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

function parseSeconds(d) { return d.endsWith('m') ? parseInt(d) * 60 : parseInt(d); }

export function setup() {
    // Warm the origin's caches so we measure steady-state, not
    // first-request-cold. Same filter+search coverage as grid.js.
    for (const s of STATUSES) for (const c of CATS) {
        const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&status=${s}&productCategory=${c}`, { headers });
        if (r.status !== 200) throw new Error(`warmup ${s}/${c}: ${r.status}`);
    }
    for (const q of SEARCH_TERMS) {
        const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&q=${q}`, { headers });
        if (r.status !== 200) throw new Error(`warmup ${q}: ${r.status}`);
    }
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
    const q = SEARCH_TERMS[Math.floor(Math.random() * SEARCH_TERMS.length)];
    const r = http.get(`${BASE}/api/v1/inventory?pageSize=100&q=${q}`, { headers });
    t_search.add(r.timings.duration);
    check(r, { 'search 200': (x) => x.status === 200 });
}
