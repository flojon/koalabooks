using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

public static class WasmClientSeeder
{
    public const string ClientId = "koalabooks-wasm";

    public static async Task SeedAsync(IServiceProvider services, Uri baseUri)
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
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
            },
            Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
            },
        };
        descriptor.RedirectUris.Add(new Uri(baseUri, "authentication/login-callback"));
        descriptor.PostLogoutRedirectUris.Add(new Uri(baseUri, "authentication/logout-callback"));

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
