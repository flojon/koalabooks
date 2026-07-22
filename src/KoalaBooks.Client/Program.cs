using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

KoalaBooks.Client.TrimmerPreserve.Preserve();

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// MainLayout and the WASM-rendered pages use MudBlazor components (MudDrawer, MudSnackbar,
// etc.) that depend on services this registers (IBrowserViewportService, popover/dialog
// services, ...).
builder.Services.AddMudServices();

// Standard authorization-code + PKCE flow against the same-origin OpenIddict authority.
// Tokens live in sessionStorage via oidc-client-js defaults; no refresh-token grant, so the
// session ends when the tab/browser closes (see Global Constraints in the plan).
builder.Services.AddOidcAuthentication(options =>
{
    options.ProviderOptions.Authority = builder.HostEnvironment.BaseAddress;
    options.ProviderOptions.ClientId = "koalabooks-wasm";
    options.ProviderOptions.ResponseType = "code";
    options.ProviderOptions.DefaultScopes.Add("email");
    options.ProviderOptions.DefaultScopes.Add("profile");
});

// Same-origin API, so the only authorized URL is the app's own base address.
builder.Services.AddHttpClient("KoalaBooks.Api", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler(sp =>
    {
        var handler = sp.GetRequiredService<AuthorizationMessageHandler>();
        handler.ConfigureHandler(authorizedUrls: [builder.HostEnvironment.BaseAddress]);
        return handler;
    });
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("KoalaBooks.Api"));

// PoC scope: only the services the WASM-rendered /review page needs.
builder.Services.AddScoped<IFiscalYearService, FiscalYearApiService>();
builder.Services.AddScoped<IAccountService, AccountApiService>();
builder.Services.AddScoped<IJournalEntryService, JournalEntryApiService>();
// MainLayout's nav badge counts resolve these via ScopeFactory regardless of which page
// is rendering, so they're needed even though no WASM page injects them directly.
builder.Services.AddScoped<IBankImportService, BankImportApiService>();
builder.Services.AddScoped<ISupplierInvoiceService, SupplierInvoiceApiService>();
// #290: no WASM page injects these yet (render-mode flips are a separate follow-up),
// but the REST endpoints they depend on now exist, so registering them here keeps
// the DI-swap pattern consistent and ready for whenever a page needs them.
builder.Services.AddScoped<ICustomerService, CustomerApiService>();
builder.Services.AddScoped<ICustomerInvoiceService, CustomerInvoiceApiService>();
builder.Services.AddScoped<ISieExportService, SieExportApiService>();

await builder.Build().RunAsync();
