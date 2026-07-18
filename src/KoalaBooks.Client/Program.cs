using KoalaBooks.Application.Services;
using KoalaBooks.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// SPIKE (#292): AddAuthenticationStateDeserialization() removed so AddOidcAuthentication()'s
// RemoteAuthenticationService is the sole AuthenticationStateProvider - no DI slot conflict.
// Only valid once nothing needs the server-prerendered auth state anymore (see /review's
// rendermode below).

// MainLayout and the WASM-rendered pages use MudBlazor components (MudDrawer, MudSnackbar,
// etc.) that depend on services this registers (IBrowserViewportService, popover/dialog
// services, ...). The Server host registers these separately in its own Program.cs.
builder.Services.AddMudServices();

builder.Services.AddOidcAuthentication(options =>
{
    // Same-origin: the Client project is always served from the Web app's own address.
    options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress;
    options.ProviderOptions.ClientId = "koalabooks-wasm";
    options.ProviderOptions.ResponseType = "code";
    options.ProviderOptions.DefaultScopes.Add("email");
});

// Same-origin API, so the only authorized URL is the app's own base address.
builder.Services.AddHttpClient("KoalaBooks.Api", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler(sp => sp.GetRequiredService<AuthorizationMessageHandler>()
        .ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]));
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("KoalaBooks.Api"));

// PoC scope: only the services the WASM-rendered /review page needs.
builder.Services.AddScoped<IFiscalYearService, FiscalYearApiService>();
builder.Services.AddScoped<IAccountService, AccountApiService>();
builder.Services.AddScoped<IJournalEntryService, JournalEntryApiService>();

await builder.Build().RunAsync();
