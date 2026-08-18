// SQLite-WASM bridge for the Blazor local replica.
//
// This is deliberately a *thin* layer: the C# side (Services/LocalReplica.cs)
// owns all schema + query text; JS just marshals sql/params across the
// interop boundary and hands back JSON. Bulk paths take a single JSON blob
// so we cross the boundary O(1) times per batch instead of O(rows).
//
// Persistence: uses the SAH-pool VFS ("opfs-sahpool"). Works from the main
// thread on every modern browser, needs no COOP/COEP headers, and persists
// under Origin Private File System. First open is ~200 ms; subsequent opens
// see the DB already there.
//
// We keep the sqlite3 module + open DB on window.bruinDb.* so hot-reload in
// dev doesn't leak file handles.

(function () {
    const SQLITE_MODULE_PATH = "vendor/sqlite/jswasm/sqlite3.mjs";
    const VFS_NAME = "opfs-sahpool";

    let sqlite3 = null;      // the top-level SQLite JS API namespace
    let poolUtil = null;     // SAH-pool utility (for wipe / removeDb / etc.)
    let dbs = new Map();     // dbName -> { db, path }

    async function ensureSqlite() {
        if (sqlite3) return sqlite3;
        // Absolute URL so `import()` from a classic script still resolves
        // against the app base; base href is set to "/" in index.html.
        const mod = await import(new URL(SQLITE_MODULE_PATH, document.baseURI).toString());
        sqlite3 = await mod.default({
            print: (m) => console.log("[sqlite]", m),
            printErr: (m) => console.warn("[sqlite]", m),
        });
        // Install the SAH-pool VFS once. `installOpfsSAHPoolVfs` returns a
        // "poolUtil" with wipeFiles/removeVfs helpers we may want later.
        // The vendored bundle sometimes exposes it under `installOpfsSAHPoolVfs`
        // directly, sometimes under `sqlite3.installOpfsSAHPoolVfs`.
        const install =
            sqlite3.installOpfsSAHPoolVfs ||
            (sqlite3.oo1 && sqlite3.oo1.installOpfsSAHPoolVfs);
        if (typeof install === "function") {
            try {
                poolUtil = await install({ name: VFS_NAME });
            } catch (e) {
                console.warn("[sqlite] OPFS SAH pool unavailable, falling back to memory:", e);
                poolUtil = null;
            }
        }
        return sqlite3;
    }

    function openInternal(dbName) {
        if (dbs.has(dbName)) return dbs.get(dbName);
        // SAH-pool DBs are named with a leading `/`. If the pool isn't
        // available (older Safari, private browsing) fall through to the
        // in-memory OO1 constructor so the app still works.
        let db;
        if (poolUtil && sqlite3.oo1.OpfsSAHPoolDb) {
            db = new sqlite3.oo1.OpfsSAHPoolDb("/" + dbName);
        } else {
            db = new sqlite3.oo1.DB(":memory:", "ct");
        }
        const entry = { db, path: db.filename || (":memory:") };
        dbs.set(dbName, entry);
        return entry;
    }

    // Convert a row array from db.exec({rowMode:'object'}) to plain JS
    // objects. The lib already emits objects; this shim exists so a future
    // switch to rowMode:'array' doesn't ripple through the C# side.
    function rowsAsJson(rows) {
        return JSON.stringify(rows ?? []);
    }

    window.bruinDb = {
        // Open (or attach to) a per-tenant DB. Returns { path } for the
        // status bar. Idempotent — repeated calls with the same name reuse
        // the open handle.
        open: async (dbName) => {
            await ensureSqlite();
            const { path } = openInternal(dbName);
            return { path };
        },

        // Fire-and-forget DDL / one-shot statements. `sql` may contain
        // multiple `;`-separated statements.
        exec: async (dbName, sql) => {
            await ensureSqlite();
            const { db } = openInternal(dbName);
            db.exec(sql);
        },

        // Parameterized query returning JSON rows. Params is either an
        // array (positional `?` binds) or an object (`$name` binds).
        query: async (dbName, sql, params) => {
            await ensureSqlite();
            const { db } = openInternal(dbName);
            const rows = db.exec({
                sql,
                bind: params ?? [],
                rowMode: "object",
                returnValue: "resultRows",
            });
            return rowsAsJson(rows);
        },

        // Scalar query — first column of the first row. Returns null if
        // no rows. Used for COUNT(*) / MAX(updated_at) / etc.
        scalar: async (dbName, sql, params) => {
            await ensureSqlite();
            const { db } = openInternal(dbName);
            const rows = db.exec({
                sql,
                bind: params ?? [],
                rowMode: "array",
                returnValue: "resultRows",
            });
            if (!rows || rows.length === 0) return null;
            const first = rows[0];
            return first.length > 0 ? first[0] : null;
        },

        // Bulk upsert path — one JSON payload per batch, single prepared
        // statement inside a transaction. This is the perf-critical hot
        // spot: hydration streams thousands of rows and every C# → JS
        // round-trip has fixed cost.
        //
        // rowsJson is a stringified array of SnapshotRow objects; the
        // caller (LocalReplica.cs) already shapes it to match the local
        // schema (snake_case fields, ISO timestamps).
        bulkUpsert: async (dbName, rowsJson) => {
            await ensureSqlite();
            const { db } = openInternal(dbName);
            const rows = JSON.parse(rowsJson);
            if (!rows.length) return 0;

            // Single INSERT ... ON CONFLICT UPDATE.  Column order must
            // match the schema declared in LocalReplica.EnsureSchemaSql.
            const sql = `
                INSERT INTO inventory (
                    id, service_number, product_category, product_name, status,
                    city, state, address, assignee, notes,
                    created_at, updated_at, row_version, deleted_at
                ) VALUES (
                    $id, $service_number, $product_category, $product_name, $status,
                    $city, $state, $address, $assignee, $notes,
                    $created_at, $updated_at, $row_version, $deleted_at
                )
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
            let count = 0;
            db.exec("BEGIN");
            try {
                const stmt = db.prepare(sql);
                try {
                    for (const r of rows) {
                        stmt.bind({
                            $id: r.id,
                            $service_number: r.service_number ?? "",
                            $product_category: r.product_category ?? "",
                            $product_name: r.product_name ?? "",
                            $status: r.status ?? "",
                            $city: r.city ?? null,
                            $state: r.state ?? null,
                            $address: r.address ?? null,
                            $assignee: r.assignee ?? null,
                            $notes: r.notes ?? null,
                            $created_at: r.created_at,
                            $updated_at: r.updated_at,
                            $row_version: r.row_version ?? 0,
                            $deleted_at: r.deleted_at ?? null,
                        });
                        stmt.stepReset();
                        count++;
                    }
                } finally {
                    stmt.finalize();
                }
                db.exec("COMMIT");
            } catch (e) {
                db.exec("ROLLBACK");
                throw e;
            }
            return count;
        },

        // Remove a DB entirely (used on tenant switch if you want a clean
        // wipe). Best-effort; ignore errors if the file isn't there.
        wipe: async (dbName) => {
            await ensureSqlite();
            const entry = dbs.get(dbName);
            if (entry) {
                try { entry.db.close(); } catch {}
                dbs.delete(dbName);
            }
            if (poolUtil && typeof poolUtil.unlink === "function") {
                try { await poolUtil.unlink("/" + dbName); } catch {}
            }
        },
    };
})();
