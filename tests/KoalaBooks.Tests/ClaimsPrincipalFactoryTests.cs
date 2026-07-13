using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Tests;

// Regression test: the factory used to skip RoleManager entirely, so IsInRole("Admin")
// was always false no matter what roles a user actually had.
public class ClaimsPrincipalFactoryTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;

    public ClaimsPrincipalFactoryTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentUser, LocalCurrentUser>();
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr));
        services.AddIdentity<ApplicationUser, IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<ApplicationUserClaimsPrincipalFactory>();

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task GenerateClaims_ForUserInAdminRole_IncludesRoleClaim()
    {
        using var scope = _sp.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        await roleManager.CreateAsync(new IdentityRole("Admin"));
        var user = new ApplicationUser { UserName = "admin@test.com", Email = "admin@test.com" };
        await userManager.CreateAsync(user, "ValidPass123!");
        await userManager.AddToRoleAsync(user, "Admin");

        var principal = await factory.CreateAsync(user);

        Assert.True(principal.IsInRole("Admin"));
    }

    [Fact]
    public async Task GenerateClaims_ForUserWithoutRole_IsNotInAdminRole()
    {
        using var scope = _sp.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        var user = new ApplicationUser { UserName = "plain@test.com", Email = "plain@test.com" };
        await userManager.CreateAsync(user, "ValidPass123!");

        var principal = await factory.CreateAsync(user);

        Assert.False(principal.IsInRole("Admin"));
    }

    [Fact]
    public async Task GenerateClaims_StillIncludesOrgIdClaim()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        var org = new Organisation { Name = "Test AB", Slug = "test-ab", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser { UserName = "org@test.com", Email = "org@test.com", OrganisationId = org.Id };
        await userManager.CreateAsync(user, "ValidPass123!");

        var principal = await factory.CreateAsync(user);

        Assert.Equal(org.Id.ToString(), principal.FindFirst("org_id")?.Value);
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }
}
