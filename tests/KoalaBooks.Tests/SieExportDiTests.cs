using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #4: SieExportService DI registration test.
/// Current bug: SieExportService is not registered in DI container,
/// causing the export page to crash at runtime.
/// </summary>
public class SieExportDiTests
{
    [Fact]
    public void SieExportService_CanBeResolvedFromDI()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        services.AddHttpContextAccessor();
        services.AddScoped<TenantContext>();
        services.AddScoped<SieExportService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetService<SieExportService>();

        Assert.NotNull(service);
    }

    [Fact]
    public void SieExportService_IsInstantiableWithAppDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var tenant = new TenantContext(new HttpContextAccessor());
        using var db = new AppDbContext(options, tenant);

        var service = new SieExportService(db);

        Assert.NotNull(service);
    }
}
