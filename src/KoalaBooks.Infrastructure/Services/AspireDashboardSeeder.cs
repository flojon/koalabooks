using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

public static class AspireDashboardSeeder
{
    public static async Task SeedAsync(IServiceProvider services, Uri redirectUri, string clientSecret)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();

        if (await manager.FindByClientIdAsync("aspire-dashboard") is not null)
            return;

        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = "aspire-dashboard",
            ClientSecret = clientSecret,
            DisplayName = "Aspire Dashboard",
            RedirectUris = { redirectUri },
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
            }
        });
    }
}
