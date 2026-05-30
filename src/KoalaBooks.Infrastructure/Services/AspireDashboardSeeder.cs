using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

public static class AspireDashboardSeeder
{
    public static async Task SeedAsync(IServiceProvider services, Uri redirectUri, string clientSecret)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = "aspire-dashboard",
            ClientType = OpenIddictConstants.ClientTypes.Confidential,
            ClientSecret = clientSecret,
            DisplayName = "Aspire Dashboard",
            RedirectUris = { redirectUri },
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            }
        };

        var existing = await manager.FindByClientIdAsync("aspire-dashboard");
        if (existing is null)
            await manager.CreateAsync(descriptor);
        else
            await manager.UpdateAsync(existing, descriptor);
    }
}
