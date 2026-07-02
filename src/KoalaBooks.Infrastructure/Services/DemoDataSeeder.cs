using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Infrastructure.Services;

public static class DemoDataSeeder
{
    public const string DemoUserEmail = "admin@koalabooks.local";
    public const string DemoUserPassword = "Admin123!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(DemoUserEmail) is not null)
            return;

        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        var tenant = new LocalCurrentUser();
        await using var db = new AppDbContext(options, tenant);

        var org = await db.Organisations.FirstOrDefaultAsync(o => o.Slug == "demo");
        if (org is null)
        {
            org = new Organisation { Name = "Demo AB", Slug = "demo", LegalForm = LegalForm.Aktiebolag };
            db.Organisations.Add(org);
            await db.SaveChangesAsync();
        }
        tenant.OrganisationId = org.Id;

        var demoUser = new ApplicationUser
        {
            UserName = DemoUserEmail,
            Email = DemoUserEmail,
            EmailConfirmed = true,
            DisplayName = "Admin",
            OrganisationId = org.Id
        };
        var createResult = await userManager.CreateAsync(demoUser, DemoUserPassword);
        if (!createResult.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create demo user: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");

        var year = DateTime.UtcNow.Year;
        var fiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = year.ToString(),
            StartDate = new DateOnly(year, 1, 1),
            EndDate = new DateOnly(year, 12, 31),
            IsClosed = false
        };
        db.FiscalYears.Add(fiscalYear);
        await db.SaveChangesAsync();

        await new BasImportService(db).ImportDefaultAsync(fiscalYear.Id);
    }
}
