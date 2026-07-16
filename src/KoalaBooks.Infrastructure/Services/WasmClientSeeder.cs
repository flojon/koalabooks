using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

public static class WasmClientSeeder
{
    public const string ClientId = "koalabooks-wasm";

    public static async Task SeedAsync(IServiceProvider services, Uri redirectUri)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var logger = services.GetRequiredService<ILoggerFactory>()
                             .CreateLogger(typeof(WasmClientSeeder));

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            DisplayName = "KoalaBooks WASM client",
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            },
            Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange
            }
        };

        descriptor.RedirectUris.Add(redirectUri);

        var existing = await manager.FindByClientIdAsync(ClientId);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor);
            logger.LogInformation("Created OpenIddict client '{ClientId}' with redirect URI {RedirectUri}", ClientId, redirectUri);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor);
            logger.LogInformation("Updated OpenIddict client '{ClientId}' with redirect URI {RedirectUri}", ClientId, redirectUri);
        }
    }
}
