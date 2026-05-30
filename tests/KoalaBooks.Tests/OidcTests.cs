using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace KoalaBooks.Tests;

public class OidcClientSeedingTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;
    private static readonly Uri DashboardRedirectUri = new("http://localhost:18888/signin-oidc");

    public OidcClientSeedingTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser>(new LocalCurrentUser());
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr));
        services.AddOpenIddict()
            .AddCore(opts => opts.UseEntityFrameworkCore().UseDbContext<AppDbContext>());

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task SeedAsync_CreatesAspireDashboardClient()
    {
        using var scope = _sp.CreateScope();
        await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync("aspire-dashboard");

        Assert.NotNull(app);
    }

    [Fact]
    public async Task SeedAsync_ClientHasExpectedPermissions()
    {
        using var scope = _sp.CreateScope();
        await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync("aspire-dashboard"))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.RefreshToken, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Authorization, descriptor.Permissions);
        Assert.Contains(OpenIddictConstants.Permissions.Endpoints.Token, descriptor.Permissions);
        Assert.Contains(
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            descriptor.Permissions);
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, DashboardRedirectUri, "test-secret");

        using var verifyScope = _sp.CreateScope();
        var manager = verifyScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var count = await manager.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SeedAsync_UpdatesExistingClient()
    {
        var originalUri = new Uri("http://localhost:18888/signin-oidc");
        var updatedUri = new Uri("http://localhost:19999/signin-oidc");

        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, originalUri, "secret-v1");

        using (var scope = _sp.CreateScope())
            await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, updatedUri, "secret-v2");

        using var verifyScope = _sp.CreateScope();
        var manager = verifyScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = (await manager.FindByClientIdAsync("aspire-dashboard"))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(descriptor, app);

        Assert.Equal(1, await manager.CountAsync());
        Assert.Contains(updatedUri, descriptor.RedirectUris);
        Assert.DoesNotContain(originalUri, descriptor.RedirectUris);
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }
}
