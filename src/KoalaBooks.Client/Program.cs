using KoalaBooks.Application.Services;
using KoalaBooks.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthenticationStateDeserialization();

// MainLayout and the WASM-rendered pages use MudBlazor components (MudDrawer, MudSnackbar,
// etc.) that depend on services this registers (IBrowserViewportService, popover/dialog
// services, ...). The Server host registers these separately in its own Program.cs.
builder.Services.AddMudServices();

// #292: AddOidcAuthentication()'s RemoteAuthenticationService used to attach bearer tokens here,
// but it claims the same AuthenticationStateProvider DI slot as AddAuthenticationStateDeserialization()
// above (needed for <AuthorizeView> to reflect the server-persisted login without a second sign-in).
// CookieBridgeTokenHandler bridges the ambient Identity cookie to a bearer token instead, via a
// dedicated OAuth grant type (see KoalaBooks.Domain.Auth.WasmCookieBridge), sidestepping that slot
// entirely.
builder.Services.AddHttpClient("KoalaBooks.TokenBridge",
    client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));
builder.Services.AddTransient<CookieBridgeTokenHandler>();

// Same-origin API, so the only authorized URL is the app's own base address.
builder.Services.AddHttpClient("KoalaBooks.Api", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<CookieBridgeTokenHandler>()
    // CookieBridgeTokenHandler caches its minted token in memory; keep the same instance for the
    // life of the app instead of losing the cache to IHttpClientFactory's periodic handler rotation.
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("KoalaBooks.Api"));

// PoC scope: only the services the WASM-rendered /review page needs.
builder.Services.AddScoped<IFiscalYearService, FiscalYearApiService>();
builder.Services.AddScoped<IAccountService, AccountApiService>();
builder.Services.AddScoped<IJournalEntryService, JournalEntryApiService>();

await builder.Build().RunAsync();
