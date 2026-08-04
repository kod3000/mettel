#!/bin/sh
# Runs once, on first initdb of the primary. Adds the pg_hba entry that lets the
# replica stream WAL. POSTGRES_HOST_AUTH_METHOD=trust only writes the `host all`
# line — replication needs its own entry.
set -e

echo "host replication all all trust" >> "$PGDATA/pg_hba.conf"
