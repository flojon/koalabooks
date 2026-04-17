using KoalaBooks.Application.Services;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using KoalaBooks.Web.Components;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using MudBlazor.Services;
using System.Text;

// Register CP437 encoding provider early so JsiSie uses it for SIE file parsing
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<AppDbContext>("koalabooks");

builder.Services.AddScoped<SieImportService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<FiscalYearService>();
builder.Services.AddScoped<JournalEntryService>();
builder.Services.AddScoped<SieExportService>();
builder.Services.AddScoped<YearEndClosingService>();
builder.Services.AddScoped<BasImportService>();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.MaxDisplayedSnackbars = 3;
    config.SnackbarConfiguration.VisibleStateDuration = 3000;
    config.SnackbarConfiguration.HideTransitionDuration = 300;
    config.SnackbarConfiguration.ShowTransitionDuration = 300;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.MapDefaultEndpoints();

// Auto-migrate on startup (retry for Aspire database creation race)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
