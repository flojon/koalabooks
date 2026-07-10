using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoalaBooks.Infrastructure.Services;

// Stopgap until there's a real UI to assign roles.
public static class AdminRoleSeeder
{
    public const string RoleName = "Admin";

    public static async Task SeedAsync(IServiceProvider services, string? adminEmail)
    {
        if (string.IsNullOrWhiteSpace(adminEmail))
            return;

        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminRoleSeeder));

        if (!await roleManager.RoleExistsAsync(RoleName))
        {
            await roleManager.CreateAsync(new IdentityRole(RoleName));
            logger.LogInformation("Created '{Role}' role", RoleName);
        }

        var user = await userManager.FindByEmailAsync(adminEmail);
        if (user is null)
        {
            logger.LogWarning(
                "Admin seed email {Email} does not match any registered user - " +
                "skipping Admin role grant until that account exists", adminEmail);
            return;
        }

        if (!await userManager.IsInRoleAsync(user, RoleName))
        {
            await userManager.AddToRoleAsync(user, RoleName);
            logger.LogInformation("Granted '{Role}' role to {Email}", RoleName, adminEmail);
        }
    }
}
