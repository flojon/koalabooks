using KoalaBooks.Application.Services;
using Scalar.AspNetCore;
using Hangfire;
using Hangfire.PostgreSql;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using KoalaBooks.Web.Components;
using KoalaBooks.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using Npgsql;
using OpenIddict.Abstractions;
using QuestPDF.Infrastructure;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;

// Register CP437 encoding provider early so JsiSie uses it for SIE file parsing
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Unpooled: AppDbContext's scoped ICurrentUser ctor dependency can't be resolved by a
// pooled context's activator, which only has access to the root provider.
var koalabooksConnectionString = builder.Configuration.GetConnectionString("koalabooks")!;
var dbPasswordFile = Environment.GetEnvironmentVariable("KOALABOOKS_DB_PASSWORD_FILE");
if (!string.IsNullOrEmpty(dbPasswordFile))
{
    koalabooksConnectionString = new NpgsqlConnectionStringBuilder(koalabooksConnectionString)
    {
        Password = File.ReadAllText(dbPasswordFile).Trim()
    }.ConnectionString;
}
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(koalabooksConnectionString));
builder.EnrichNpgsqlDbContext<AppDbContext>();

builder.Services.AddDataProtection()
    .SetApplicationName("KoalaBooks")
    .PersistKeysToDbContext<AppDbContext>();

// Excluded from Testing: eager Postgres schema-prep here corrupts EnsureCreated()'s
// schema visibility under WebApplicationFactory (and exhausts connections under load).
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(koalabooksConnectionString)));
    builder.Services.AddHangfireServer();
    builder.Services.AddScoped<KoalaBooks.Application.Jobs.DocumentExtractionJob>();
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue,
        KoalaBooks.Application.Jobs.HangfireDocumentExtractionQueue>();
}
else
{
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue,
        KoalaBooks.Application.Jobs.NoOpDocumentExtractionQueue>();
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.SignIn.RequireConfirmedAccount = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.AllowedForNewUsers = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders()
.AddClaimsPrincipalFactory<ApplicationUserClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
               .UseDbContext<AppDbContext>();
    })
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("/connect/authorize");
        options.SetTokenEndpointUris("/connect/token");
        options.AllowPasswordFlow()
               .AllowRefreshTokenFlow()
               .AllowAuthorizationCodeFlow();
        options.AcceptAnonymousClients();
        // Scopes other than "openid"/"offline_access" must be registered here, or OpenIddict
        // rejects them with invalid_scope even when the client has the matching permission.
        options.RegisterScopes(
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Email);
        options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            options.AddDevelopmentEncryptionCertificate()
                   .AddDevelopmentSigningCertificate();
            // Emit plain JWTs in dev/test so the payload is readable without decryption.
            options.DisableAccessTokenEncryption();
        }
        else
        {
            // Ephemeral keys invalidate all tokens on restart and cannot be shared across
            // instances. Replace with Key Vault / environment certificates before production.
            options.AddEphemeralEncryptionKey()
                   .AddEphemeralSigningKey();
        }
        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .DisableTransportSecurityRequirement();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

builder.Services.AddScoped<ISieImportService, SieImportService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IFiscalYearService, FiscalYearService>();
builder.Services.AddScoped<JournalEntryService>();
builder.Services.AddScoped<IJournalEntryService>(sp => sp.GetRequiredService<JournalEntryService>());
builder.Services.AddScoped<IJournalEntryReportingService>(sp => sp.GetRequiredService<JournalEntryService>());
builder.Services.AddScoped<IVoucherGapService, VoucherGapService>();
builder.Services.AddScoped<ISieExportService, SieExportService>();
builder.Services.AddScoped<IYearEndClosingService, YearEndClosingService>();
builder.Services.AddScoped<IBasImportService, BasImportService>();
builder.Services.AddScoped<IAccountMappingService, AccountMappingService>();
builder.Services.AddScoped<IBankImportService, BankImportService>();
builder.Services.AddScoped<ISupplierInvoiceService, SupplierInvoiceService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<ICustomerInvoiceService, CustomerInvoiceService>();
builder.Services.AddScoped<IOrganisationService, OrganisationService>();
builder.Services.AddScoped<KoalaBooks.Infrastructure.Services.FilenameExtractor>();
builder.Services.AddScoped<KoalaBooks.Infrastructure.Services.PdfTextExtractor>();
builder.Services.AddScoped<KoalaBooks.Infrastructure.Services.CompositeExtractor>();
builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentExtractor>(sp =>
    sp.GetRequiredService<KoalaBooks.Infrastructure.Services.CompositeExtractor>());
builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentStorage,
    KoalaBooks.Infrastructure.Services.DbDocumentStorage>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IDocumentProvider, WebDocumentProvider>();
builder.Services.AddSingleton<IVatReportCsvExporter, VatReportCsvExporter>();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
    config.SnackbarConfiguration.VisibleStateDuration = 3000;
    config.SnackbarConfiguration.HideTransitionDuration = 300;
    config.SnackbarConfiguration.ShowTransitionDuration = 300;
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddAuthenticationStateSerialization(options => options.SerializeAllClaims = true);

builder.Services.AddAuthorization();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 8));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128));
    // Docker bridge networks (Caddy runs on 172.x.x.x when deployed via docker-compose)
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
});

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 10,
                QueueLimit = 0
            }));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    app.Logger.LogWarning(
        "OpenIddict is using ephemeral encryption/signing keys. " +
        "Tokens will be invalidated on restart and cannot be shared across instances. " +
        "Configure persistent Key Vault certificates before production.");

app.MapDefaultEndpoints();

app.MapGet("/documents/{id:int}", async (int id, IDocumentService svc) =>
{
    var result = await svc.GetDownloadAsync(id);
    return result is null
        ? Results.NotFound()
        : Results.File(result.Value.Data, result.Value.ContentType);
}).RequireAuthorization();

if (!app.Environment.IsEnvironment("Testing"))
{
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireDashboardAuthorizationFilter()]
    });
}

app.MapGet("/customer-invoices/{id:int}/pdf", async (int id, ICustomerInvoiceService svc) =>
{
    var invoice = await svc.GetByIdAsync(id);
    if (invoice is null) return Results.NotFound();
    var bytes = KoalaBooks.Web.Services.CustomerInvoicePdfGenerator.Generate(invoice);
    var filename = $"Faktura-{invoice.InvoiceNumber}.pdf";
    return Results.File(bytes, "application/pdf", filename);
}).RequireAuthorization();

// Auto-migrate and seed on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
    {
        db.Database.EnsureCreated();
    }
    else
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        if (!app.Environment.IsProduction() &&
            (app.Environment.IsDevelopment() || builder.Configuration["SEED_DEMO_DATA"] == "true"))
        {
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);
        }

        var dashboardRedirectUri = builder.Configuration["AspireDashboard:OidcRedirectUri"]
            ?? "http://localhost:18888/";
        var dashboardClientSecret = builder.Configuration["AspireDashboard:OidcClientSecret"]
            ?? "aspire-dashboard-dev-secret";
        await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, new Uri(dashboardRedirectUri), dashboardClientSecret);

        var wasmClientRedirectUri = builder.Configuration["WasmClient:RedirectUri"]
            ?? "https://localhost:7154/authentication/login-callback";
        await WasmClientSeeder.SeedAsync(scope.ServiceProvider, new Uri(wasmClientRedirectUri));

        // Stopgap until there's a real UI to grant roles.
        await AdminRoleSeeder.SeedAsync(scope.ServiceProvider, builder.Configuration["AdminSeed:Email"]);
    }
}

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("sv-SE")
    .AddSupportedCultures("sv-SE")
    .AddSupportedUICultures("sv-SE"));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(KoalaBooks.Components.Pages.Home).Assembly);

app.MapOpenApi();
if (app.Environment.IsDevelopment())
    app.MapScalarApiReference();
app.MapControllers();

app.Run();

public partial class Program { }
