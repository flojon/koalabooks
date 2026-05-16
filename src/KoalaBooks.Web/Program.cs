using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using KoalaBooks.Web.Components;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using QuestPDF.Infrastructure;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;

// Register CP437 encoding provider early so JsiSie uses it for SIE file parsing
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<AppDbContext>("koalabooks");

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TenantContext>();

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
        options.SetTokenEndpointUris("/connect/token");
        options.AllowPasswordFlow()
               .AllowRefreshTokenFlow();
        if (builder.Environment.IsDevelopment())
        {
            options.AddDevelopmentEncryptionCertificate()
                   .AddDevelopmentSigningCertificate();
        }
        else
        {
            // Ephemeral keys invalidate all tokens on restart and cannot be shared across
            // instances. Replace with Key Vault / environment certificates before production.
            options.AddEphemeralEncryptionKey()
                   .AddEphemeralSigningKey();
        }
        options.UseAspNetCore()
               .EnableTokenEndpointPassthrough();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

builder.Services.AddScoped<SieImportService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<FiscalYearService>();
builder.Services.AddScoped<JournalEntryService>();
builder.Services.AddScoped<SieExportService>();
builder.Services.AddScoped<YearEndClosingService>();
builder.Services.AddScoped<BasImportService>();
builder.Services.AddScoped<BankImportService>();
builder.Services.AddScoped<SupplierInvoiceService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<CustomerInvoiceService>();
builder.Services.AddScoped<AttachmentService>();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
    config.SnackbarConfiguration.VisibleStateDuration = 3000;
    config.SnackbarConfiguration.HideTransitionDuration = 300;
    config.SnackbarConfiguration.ShowTransitionDuration = 300;
});

builder.Services.AddRazorPages();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAuthorization();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // Trust the loopback proxy (nginx on the same host). In production, restrict to your
    // actual proxy IP/subnet by adding to KnownProxies or KnownIPNetworks instead.
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 8));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128));
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

app.MapGet("/attachments/{id:int}", async (int id, AttachmentService svc) =>
{
    var a = await svc.GetAsync(id);
    return a is null ? Results.NotFound() : Results.File(a.Data, a.ContentType, a.FileName);
}).RequireAuthorization();

app.MapGet("/customer-invoices/{id:int}/pdf", async (int id, CustomerInvoiceService svc) =>
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

        if (app.Environment.IsDevelopment())
        {
            // Seed a default org + dev user if none exists
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            const string devEmail = "admin@koalabooks.local";
            if (await userManager.FindByEmailAsync(devEmail) is null)
            {
                var org = new Organisation { Name = "Dev Organisation", Slug = "dev" };
                db.Organisations.Add(org);
                await db.SaveChangesAsync();

                var devUser = new ApplicationUser
                {
                    UserName = devEmail,
                    Email = devEmail,
                    EmailConfirmed = true,
                    DisplayName = "Admin",
                    OrganisationId = org.Id
                };
                await userManager.CreateAsync(devUser, "Admin123!");
            }
        }
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
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program { }
