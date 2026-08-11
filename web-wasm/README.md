# web-wasm — Blazor WebAssembly twin of the React SPA

A second frontend for the Bruin Inventory Grid, built in
**.NET 9 Blazor WebAssembly**. Consumes the same API as
[`apps/web/`](../apps/web/README.md) so we can compare the two
implementations under an identical workload.

## Why does this exist?

To measure the delta between a modern React SPA and a Blazor WASM SPA
against the same backend, on the same data, doing the same work. Both
apps ship:

- Server-side keyset pagination over 5M inventory rows across 3 tenants.
- Filter (status / category), 300ms-debounced full-text search.
- Sort by any indexed column.
- Row-detail drawer with status PATCH (rowVersion optimistic concurrency).
- Read-your-own-writes protocol (`X-Min-LSN` / `X-Write-LSN` echo).
- Tenant switching (three demo API keys).
- Auto-recovery on 5xx (exponential backoff, banner while retrying).

The interesting axes for comparison:

| Axis                        | React SPA               | Blazor WASM           |
|-----------------------------|-------------------------|-----------------------|
| Payload (release)           | ~200 KB gzipped         | AOT ~5–10 MB gzipped  |
| First interactive           | ~200 ms                 | 1–3 s (WASM warm-up)  |
| Steady-state scroll perf    | 60 fps virtualized      | 60 fps virtualized    |
| Memory footprint            | ~40 MB                  | ~60–80 MB (WASM heap) |
| Framework runtime           | JS engine               | .NET runtime in WASM  |

The React app wins first-paint by a large margin. The interesting
question is whether Blazor WASM catches up (or beats it) once the
runtime is warm — especially on the scroll loop and the drawer
PATCH round-trip. That's what this twin is here to measure.

## Prerequisites

- .NET SDK 9.0 (`dotnet --version` should print `9.0.x`).
- The `wasm-tools` workload — only required to publish an AOT build:
  ```sh
  dotnet workload install wasm-tools
  ```
  Local `dotnet run` does not need it.
- The API container running locally (`make up` from the repo root)
  or reachable at whatever `ApiBaseUrl` is set to.

## Local dev

```sh
cd web-wasm
dotnet run
```

Serves on `http://localhost:5174`. In dev mode the client reads
`wwwroot/appsettings.Development.json` and hits `http://localhost:8081`
directly. Hot-reload works via `dotnet watch run` if you want it.

## Publish (release build with AOT)

```sh
cd web-wasm
dotnet publish -c Release -p:BlazorWasmAot=true
```

Output lands under `bin/Release/net9.0/publish/wwwroot/`. That's what
gets rsync'd to the deploy host.

Skip `-p:BlazorWasmAot=true` for a smaller/interpreted release build —
it ships faster (~2 MB) but has slower steady-state throughput. AOT is
the honest baseline for perf comparisons. (The property is our own,
gating `RunAOTCompilation` — we avoid the reserved `PublishAOT` name
because that triggers .NET Native AOT which errors on `browser-wasm`.)

## Deploy

The deploy target is `wasm.mettel.exercise.dany.codes` (sibling of the
React SPA at `mettel.exercise.dany.codes`). Both hit the same API.

1. Build the release AOT bundle (above).
2. rsync `bin/Release/net9.0/publish/wwwroot/` to the remote host at
   `/srv/mettel-wasm/web/` (or whatever your `root` directive in
   `ops/nginx/wasm.mettel.exercise.dany.codes.conf` points to).
3. Copy the nginx conf to the remote's `servers/apps/` directory.
4. Ask the box owner to `sudo nginx -s reload` — the deploy user can't
   reload nginx.

## Project layout

```
web-wasm/
  Bruin.Web.Wasm.csproj      # net9.0 Blazor WebAssembly
  Program.cs                 # DI wiring — HttpClient + handlers + services
  App.razor                  # Router root
  _Imports.razor             # Global usings
  Layout/
    MainLayout.razor         # Top bar + client picker
  Pages/
    Home.razor               # Filters + grid + drawer
  Components/
    InventoryGrid.razor      # Virtualized keyset-paginated grid
    Filters.razor            # Search + chip filters (300ms debounce)
    RowDetailDrawer.razor    # Detail + status PATCH + auto-recovery
    CountDisplay.razor       # "Table total ≈ X" + filtered count
    ClientPicker.razor       # Tenant switcher
    Field.razor              # Small label/value helper
  Services/
    BruinApiClient.cs        # Typed API wrapper
    TenantContext.cs         # Current tenant (singleton)
    LsnStore.cs              # Per-tenant WAL watermark
  Handlers/
    ApiKeyHandler.cs         # X-Api-Key + LSN echo DelegatingHandler
  Models/
    Dtos.cs                  # Records mirroring OpenAPI schemas
    Tenant.cs
    ApiException.cs
  wwwroot/
    index.html               # Blazor shell + inline boot spinner
    css/app.css              # Hand-written CSS (no Tailwind — keeps payload honest)
    js/scroll.js             # Single-purpose scroll metrics helper
    appsettings.json         # ApiBaseUrl = "" (same origin) in prod
    appsettings.Development.json  # ApiBaseUrl = "http://localhost:8081" in dev
```

## Feature parity vs. `apps/web/`

**In v1** (what's here today):
- ✅ Grid virtualization + keyset infinite scroll
- ✅ Filters (status, category), 300ms-debounced search
- ✅ Sort (server-sortable columns only)
- ✅ Row detail drawer + status PATCH
- ✅ rowVersion optimistic concurrency + 409 refetch
- ✅ 5xx auto-recovery with backoff
- ✅ Tenant switching with full tree reset
- ✅ X-Min-LSN echo / X-Write-LSN capture (read-your-own-writes)

**Deferred to v2** (parity gaps — intentional to keep v1 shippable):
- ⏳ Bulk CSV upload panel + SSE progress stream
- ⏳ Create-inventory modal
- ⏳ Saved views bar
- ⏳ State-code filter (California / Texas / etc.)

The v1 slice is enough to exercise the interesting perf-loop
components (grid + fetch + render + drawer + PATCH). Adding v2 later
is straightforward — the API client already supports every endpoint
the React app calls.

## Notes for future me

- The `<Virtualize>` component's `ItemsProvider` mode doesn't map well
  to keyset cursors (its contract is `startIndex + count`). We use the
  `Items="_rows"` mode + a scroll-position heuristic instead. Matches
  the React app's `useInfiniteQuery` behavior.
- `InvariantGlobalization=true` keeps the WASM payload small by
  skipping the ICU data blob. If we ever need locale-aware sorting or
  date formatting we'll need to flip that.
- The DTOs are hand-written; we could later swap to NSwag generation
  from `apps/api/openapi.v1.json` if the schema surface grows.
