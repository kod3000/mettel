// Bootstrap for the Blazor WebAssembly twin of the React SPA.
//
// Wires:
//   - HttpClient with base = same origin the app was served from
//     (nginx proxies /api/v1/* to the API container in prod; in dev
//     `dotnet run` proxies via VITE_API_BASE-equivalent env override).
//   - ApiKeyHandler DelegatingHandler injects X-Api-Key + X-Min-LSN and
//     captures X-Write-LSN from mutation responses (read-your-own-writes).
//   - TenantContext + LsnStore are singletons so a tenant switch flips
//     both the header and the watermark in one place.
//
// Kept intentionally minimal — the whole point of this app is to be a
// small, comparable footprint against the React SPA, not to accrete
// framework abstractions.

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Bruin.Web.Wasm;
using Bruin.Web.Wasm.Handlers;
using Bruin.Web.Wasm.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Base URL: dev override via appsettings.Development.json (ApiBaseUrl),
// otherwise same-origin as the served SPA (nginx proxies /api/v1/*).
var apiBase = builder.Configuration["ApiBaseUrl"];
var baseAddress = string.IsNullOrWhiteSpace(apiBase)
    ? new Uri(builder.HostEnvironment.BaseAddress)
    : new Uri(apiBase);

builder.Services.AddSingleton<TenantContext>();
builder.Services.AddSingleton<LsnStore>();
builder.Services.AddSingleton<ToastService>();
builder.Services.AddSingleton<ErrorReporter>();
builder.Services.AddSingleton<MeService>();
builder.Services.AddTransient<ApiKeyHandler>();

builder.Services.AddHttpClient<BruinApiClient>(c => c.BaseAddress = baseAddress)
    .AddHttpMessageHandler<ApiKeyHandler>();

await builder.Build().RunAsync();
