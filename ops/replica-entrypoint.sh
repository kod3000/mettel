#!/bin/sh
# Replica entrypoint. First boot on a fresh volume: pg_basebackup from the primary
# so we start from a byte-for-byte physical snapshot. Subsequent boots skip
# straight to the standard postgres entrypoint — WAL replay resumes from the last
# checkpoint written to disk.
set -e

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  echo "[replica] fresh volume — bootstrapping via pg_basebackup"

  # The volume was created by dockerd as root; postgres needs to own PGDATA.
  mkdir -p "$PGDATA"
  chown -R postgres:postgres "$PGDATA"
  chmod 700 "$PGDATA"

  until gosu postgres pg_isready -h pg-primary -p 5432 -U bruin >/dev/null 2>&1; do
    echo "[replica] waiting for pg-primary…"
    sleep 1
  done

  # -R writes standby.signal + primary_conninfo so postgres comes up as a hot standby.
  # -Xs streams WAL alongside the base backup so we don't miss changes made during it.
  gosu postgres pg_basebackup \
    -h pg-primary -p 5432 -U bruin \
    -D "$PGDATA" -Fp -Xs -R -P

  # hot_standby_feedback prevents the primary from vacuuming rows a long-running
  # replica query still needs — a small cost for correctness during bench runs.
  echo "hot_standby_feedback = on" >> "$PGDATA/postgresql.auto.conf"

  echo "[replica] basebackup complete — starting postgres"
fi

# Same tuning as pg-primary — the query planner uses shared_buffers,
# effective_cache_size and work_mem to pick between index scan + filter and
# bitmap heap scan. Divergent defaults produced different plans between
# primary and replica during Phase 4 (xyzzy search timed out on replica
# because its planner picked a per-row-filter index scan instead of a
# BitmapOr). PG_* env vars come from the compose file / .env.
exec docker-entrypoint.sh postgres \
  -c wal_level=replica \
  -c hot_standby=on \
  -c hot_standby_feedback=on \
  -c shared_buffers=${PG_SHARED_BUFFERS:-256MB} \
  -c effective_cache_size=${PG_EFFECTIVE_CACHE_SIZE:-1GB} \
  -c work_mem=${PG_WORK_MEM:-8MB} \
  -c max_connections=${PG_MAX_CONNECTIONS:-100} \
  -c max_parallel_workers_per_gather=${PG_PARALLEL_WORKERS:-0}
