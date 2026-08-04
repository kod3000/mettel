.PHONY: up down reset seed verify bench logs ps psql-primary psql-replica

# Phase 0 gate: `make up` + pg_is_in_recovery = t on the replica + /health/ready = 200.

up:
	docker compose up -d --build
	@echo "Waiting for /health/ready…"
	@for i in $$(seq 1 60); do \
		code=$$(curl -sf -o /dev/null -w "%{http_code}" http://localhost:8081/health/ready 2>/dev/null || echo 000); \
		if [ "$$code" = "200" ]; then echo "api ready (after $${i}s)"; exit 0; fi; \
		sleep 1; \
	done; \
	echo "api did not report ready within 60s — check 'make logs'"; exit 1

down:
	docker compose down

reset:
	docker compose down -v
	@echo "Volumes removed. Next 'make up' will re-run pg_basebackup on the replica."

logs:
	docker compose logs -f --tail=200

ps:
	docker compose ps

psql-primary:
	docker compose exec pg-primary psql -U bruin -d bruin

psql-replica:
	docker compose exec pg-replica psql -U bruin -d bruin

# --- Placeholders wired in later phases ---

seed:
	@# Runs inside the compose network as a one-shot container so it can reach
	@# pg-primary by hostname — the host's 5432 is often already bound by a
	@# native Postgres install on macOS. Overrides:
	@#   `make seed ROWS=100000` (fast path)
	@#   `make seed ROWS=100000 CLIENTS=2 WORKERS=8`
	@# --append leaves existing inventory alone; default truncates.
	docker compose --profile seed run --rm --build seed \
		--rows $${ROWS:-5000000} --clients $${CLIENTS:-3} --workers $${WORKERS:-4}

verify:
	@# Aggregate quality gate:
	@#   1. .NET build + xUnit tests (Phase 3+5+6+ …)
	@#   2. Regenerate OpenAPI + TypeScript types; fail if the tree changed
	@#      (i.e. codegen was not committed).
	@#   3. TypeScript typecheck of packages/api-types and apps/web.
	@#   4. (Later) frontend lint + build.
	dotnet build apps/api/Bruin.Api.csproj
	dotnet test tests/Bruin.Api.Tests/Bruin.Api.Tests.csproj --logger "console;verbosity=minimal" --nologo
	@# Codegen from a live API — assumes `make up` was already run so the
	@# API is reachable on localhost:8081. Guarantees the OpenAPI shipped
	@# with the repo matches what the code actually emits right now.
	curl -sf http://localhost:8081/openapi/v1.json -o apps/api/openapi.v1.json
	cd packages/api-types && npm install --silent --legacy-peer-deps && npm run --silent generate
	cd packages/api-types && npm run --silent check
	@# Fail if codegen changed anything committed — only meaningful in a
	@# git checkout, so skip cleanly outside one.
	@if git rev-parse --git-dir >/dev/null 2>&1; then \
		if ! git diff --quiet apps/api/openapi.v1.json packages/api-types/src/generated.ts 2>/dev/null; then \
			echo "verify: codegen produced changes — commit the diff below:"; \
			git diff --stat apps/api/openapi.v1.json packages/api-types/src/generated.ts; \
			exit 1; \
		fi; \
	fi
	@echo "verify: OK"

bench:
	@# Requires BRUIN_BENCH_MODE=1 on the API so /bench/offset (the only
	@# OFFSET-emitting route in the codebase) exists during the run.
	@# k6 executes on the compose network so it resolves `api` by hostname.
	@# Overrides:  make bench VUS=25 DURATION=30s DEEP=100000
	@mkdir -p bench/out
	docker compose stop api >/dev/null
	BRUIN_BENCH_MODE=1 docker compose up -d --build api
	@echo "Waiting for /health/ready with bench mode on…"
	@for i in $$(seq 1 60); do \
		code=$$(curl -sf -o /dev/null -w "%{http_code}" http://localhost:8081/health/ready 2>/dev/null || echo 000); \
		if [ "$$code" = "200" ]; then echo "api ready"; break; fi; sleep 1; \
	done
	@# Compose derives the network name from the project (usually the
	@# parent directory). Detect it dynamically so this works whether the
	@# checkout is `challenge/` (local) or `.../repo/` (remote deploy).
	NET=$$(docker network ls --format '{{.Name}}' | grep -E "_default$$" | grep -v '^bridge$$' | head -1); \
	echo "using network: $$NET"; \
	docker run --rm --network="$$NET" \
		-v "$$PWD/bench:/bench" -v "$$PWD/bench/out:/bench/out" \
		-e BENCH_VUS=$${VUS:-100} -e BENCH_DURATION=$${DURATION:-45s} \
		-e BENCH_DEEP_DEPTH=$${DEEP:-200000} \
		grafana/k6 run /bench/grid.js; \
	rc=$$?; \
	cp -f bench/out/results.md bench/results.md 2>/dev/null || true; \
	docker compose stop api >/dev/null; \
	docker compose up -d api >/dev/null; \
	echo "bench: results at bench/results.md (k6 exit=$$rc)"; \
	exit $$rc
