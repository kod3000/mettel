// SQLite-WASM bridge for the Blazor local replica.
//
// The C# side (Services/LocalReplica.cs) owns all schema + query text;
// this file just marshals sql/params across the interop boundary and
// hands back JSON.
//
// Persistence: SQLite runs in a dedicated Web Worker via the vendored
// sqlite3Worker1Promiser wrapper. This is required for OPFS —
// `FileSystemFileHandle.createSyncAccessHandle` is only exposed to
// workers, not the main thread. With COOP/COEP set (see nginx conf),
// SharedArrayBuffer + Atomics.wait become available and the worker's
// `opfs` VFS (async proxy) can install, persisting DBs across reloads
// under OPFS.
//
// Fallback: on any failure (no OPFS, insufficient isolation), we open
// `:memory:` databases so the app still works — data just doesn't
// survive a reload. `mode` in the returned open() payload lets the UI
// reflect which path is live.
//
// Note: the vendored sqlite3-worker1.js bundle registers the plain
// `opfs` VFS but NOT `opfs-sahpool` (the SAH-pool VFS requires
// installOpfsSAHPoolVfs() to be called; the bundled worker doesn't do
// that automatically). We use `opfs` because it's what's actually
// available; SAH-pool would be marginally faster but would need a
// customised worker build.

(function () {
    const VENDOR_DIR = "vendor/sqlite/jswasm/";
    const WORKER_URL = VENDOR_DIR + "sqlite3-worker1.js";
    const PROMISER_URL = VENDOR_DIR + "sqlite3-worker1-promiser.js";

    let promiser = null;              // Ready promiser function (returned by factory onready).
    let promiserPromise = null;       // In-flight init; cached so concurrent callers reuse.
    const dbIds = new Map();          // dbName -> dbId from worker's open() response.
    let mode = "unknown";             // "opfs" | "memdb" | "unknown"

    function loadClassicScript(url) {
        return new Promise((resolve, reject) => {
            const existing = document.querySelector(`script[src="${url}"]`);
            if (existing && existing.dataset.bruinLoaded === "1") { resolve(); return; }
            const s = document.createElement("script");
            s.src = url;
            s.async = true;
            s.onload = () => { s.dataset.bruinLoaded = "1"; resolve(); };
            s.onerror = () => reject(new Error(`Failed to load ${url}`));
            document.head.appendChild(s);
        });
    }

    async function ensurePromiser() {
        if (promiser) return promiser;
        if (promiserPromise) return promiserPromise;

        promiserPromise = (async () => {
            const promiserUrl = new URL(PROMISER_URL, document.baseURI).toString();
            const workerUrl = new URL(WORKER_URL, document.baseURI).toString();

            await loadClassicScript(promiserUrl);
            const factory = globalThis.sqlite3Worker1Promiser;
            if (typeof factory !== "function") {
                throw new Error("sqlite3Worker1Promiser factory not found on global scope after script load");
            }

            // Config-with-onready form (v1 API). onready fires once the
            // worker posts back its ready message. We resolve with the
            // returned promiser function itself; the factory has already
            // returned it synchronously into `inst`.
            const inst = await new Promise((resolve, reject) => {
                let created = null;
                const cfg = {
                    // Explicit worker factory so we control the URL. Same
                    // origin as the app; COEP require-corp is satisfied
                    // because both are same-origin.
                    worker: () => new Worker(workerUrl, { type: "classic" }),
                    onready: () => resolve(created),
                    onerror: (e) => {
                        console.warn("[sqlite] worker promiser error", e);
                        reject(e);
                    },
                };
                created = factory(cfg);
            });
            promiser = inst;

            // Probe available VFSes. The vendored worker registers a
            // small set: `unix`, `memdb`, and — when COOP/COEP allow SAB
            // — `opfs`. Anything else needs a custom worker build.
            try {
                const cfg = await promiser("config-get", {});
                const vfsList = cfg?.result?.vfsList ?? [];
                if (Array.isArray(vfsList) && vfsList.includes("opfs")) {
                    mode = "opfs";
                } else {
                    mode = "memdb";
                }
            } catch (e) {
                console.warn("[sqlite] config-get failed, assuming memdb", e);
                mode = "memdb";
            }
            console.info(`[sqlite] worker ready, mode=${mode}, vfs list probed`);
            return promiser;
        })();
        return promiserPromise;
    }

    async function openDb(dbName) {
        const cached = dbIds.get(dbName);
        if (cached) return cached;
        const p = await ensurePromiser();

        // `opfs` DBs live under OPFS via the async-proxy VFS; memdb
        // is per-session in RAM. On OPFS open failure (quota, permission,
        // OPFS unavailable in the browsing context), fall back to memdb
        // so the UI still functions rather than erroring out.
        const primaryFilename = mode === "opfs"
            ? `file:${dbName}?vfs=opfs`
            : `:memory:`;
        let res;
        try {
            res = await p("open", { filename: primaryFilename });
        } catch (e) {
            if (mode === "opfs") {
                console.warn(`[sqlite] OPFS open failed, falling back to memdb: ${extractErr(e)}`);
                mode = "memdb";
                res = await p("open", { filename: ":memory:" });
            } else {
                throw e;
            }
        }
        const dbId = res?.dbId ?? res?.result?.dbId;
        if (!dbId) throw new Error("open returned no dbId: " + JSON.stringify(res));
        dbIds.set(dbName, dbId);
        return dbId;
    }

    async function execRaw(dbId, sql, bind, rowMode) {
        const p = await ensurePromiser();
        const res = await p("exec", {
            dbId, sql,
            bind: bind ?? [],
            rowMode: rowMode ?? "object",
            returnValue: "resultRows",
        });
        return res?.result?.resultRows ?? [];
    }

    // Worker1 promiser rejects with the full protocol message object,
    // whose `.result` often holds the actual error. Pull the human-
    // readable string out for logs.
    function extractErr(e) {
        if (!e) return "unknown";
        if (typeof e === "string") return e;
        if (e.message) return e.message;
        const r = e.result;
        if (r && typeof r === "object") return r.message ?? JSON.stringify(r);
        try { return JSON.stringify(e); } catch { return String(e); }
    }

    // Convert a JS value to a SQL literal for embedding in bulk inserts.
    // Bulk path builds one big multi-VALUES statement per chunk instead
    // of one bind-set-per-row because worker1 exec doesn't support
    // multi-bind — one call per row would mean thousands of postMessage
    // round-trips per batch.
    function sqlLit(v) {
        if (v === null || v === undefined) return "NULL";
        if (typeof v === "number") return Number.isFinite(v) ? String(v) : "NULL";
        if (typeof v === "boolean") return v ? "1" : "0";
        // Everything else: string. Escape single quotes by doubling.
        return "'" + String(v).replaceAll("'", "''") + "'";
    }

    const BULK_COLUMNS = [
        "id", "service_number", "product_category", "product_name", "status",
        "city", "state", "address", "assignee", "notes",
        "created_at", "updated_at", "row_version", "deleted_at",
    ];

    window.bruinDb = {
        // Open (or attach to) a per-tenant DB. Returns { path, mode }
        // so the UI can label persistence state.
        open: async (dbName) => {
            await openDb(dbName);
            return { path: dbName, mode };
        },

        // Fire-and-forget DDL / one-shot statements. `sql` may contain
        // multiple `;`-separated statements.
        exec: async (dbName, sql) => {
            const dbId = await openDb(dbName);
            await execRaw(dbId, sql, [], "object");
        },

        // Parameterized query returning JSON rows.
        query: async (dbName, sql, params) => {
            const dbId = await openDb(dbName);
            const rows = await execRaw(dbId, sql, params ?? [], "object");
            return JSON.stringify(rows);
        },

        // Scalar query — first column of the first row.
        scalar: async (dbName, sql, params) => {
            const dbId = await openDb(dbName);
            const rows = await execRaw(dbId, sql, params ?? [], "array");
            if (!rows.length) return null;
            const first = rows[0];
            return first.length > 0 ? first[0] : null;
        },

        // Bulk upsert: one INSERT ... VALUES (...), (...), ... per
        // chunk with ON CONFLICT UPDATE. Chunk size keeps the SQL
        // string modest and stays well under any parser limits.
        //
        // rowsJson is a stringified array of SnapshotRow objects; the
        // caller (LocalReplica.cs) shapes it to match the schema.
        bulkUpsert: async (dbName, rowsJson) => {
            const dbId = await openDb(dbName);
            const rows = JSON.parse(rowsJson);
            if (!rows.length) return 0;

            const p = await ensurePromiser();
            const CHUNK = 500;
            let count = 0;

            for (let start = 0; start < rows.length; start += CHUNK) {
                const chunk = rows.slice(start, start + CHUNK);
                const values = chunk.map((r) => "(" + [
                    sqlLit(r.id),
                    sqlLit(r.service_number ?? ""),
                    sqlLit(r.product_category ?? ""),
                    sqlLit(r.product_name ?? ""),
                    sqlLit(r.status ?? ""),
                    sqlLit(r.city ?? null),
                    sqlLit(r.state ?? null),
                    sqlLit(r.address ?? null),
                    sqlLit(r.assignee ?? null),
                    sqlLit(r.notes ?? null),
                    sqlLit(r.created_at),
                    sqlLit(r.updated_at),
                    sqlLit(r.row_version ?? 0),
                    sqlLit(r.deleted_at ?? null),
                ].join(",") + ")").join(",");

                const sql = `
                    INSERT INTO inventory (${BULK_COLUMNS.join(", ")})
                    VALUES ${values}
                    ON CONFLICT(id) DO UPDATE SET
                        service_number = excluded.service_number,
                        product_category = excluded.product_category,
                        product_name = excluded.product_name,
                        status = excluded.status,
                        city = excluded.city,
                        state = excluded.state,
                        address = excluded.address,
                        assignee = excluded.assignee,
                        notes = excluded.notes,
                        created_at = excluded.created_at,
                        updated_at = excluded.updated_at,
                        row_version = excluded.row_version,
                        deleted_at = excluded.deleted_at
                    WHERE excluded.row_version >= inventory.row_version
                `;
                await p("exec", { dbId, sql });
                count += chunk.length;
            }
            return count;
        },

        // Close + unlink the DB entirely. Best-effort.
        wipe: async (dbName) => {
            const cached = dbIds.get(dbName);
            if (!cached) return;
            const p = await ensurePromiser();
            try { await p("close", { dbId: cached, unlink: true }); } catch {}
            dbIds.delete(dbName);
        },

        // Expose current mode for the UI ("opfs-sahpool" | "memdb" | "unknown").
        currentMode: () => mode,
    };
})();
