using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

// Public client for third-party/script REST API callers using the password grant.
// Registering a real client (instead of AcceptAnonymousClients()) lets us identify
// which application issued a token request and revoke it independently of user accounts.
public static class ApiClientSeeder
{
    public const string ClientId = "koalabooks-api";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var logger = services.GetRequiredService<ILoggerFactory>()
                             .CreateLogger(typeof(ApiClientSeeder));

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            DisplayName = "KoalaBooks API",
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.Password,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            }
        };

        var existing = await manager.FindByClientIdAsync(ClientId).ConfigureAwait(false);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor).ConfigureAwait(false);
            logger.LogInformation("Created OpenIddict client '{ClientId}'", ClientId);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor).ConfigureAwait(false);
            logger.LogInformation("Updated OpenIddict client '{ClientId}'", ClientId);
        }
    }
}
