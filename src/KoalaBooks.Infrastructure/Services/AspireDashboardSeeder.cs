using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

public static class AspireDashboardSeeder
{
    public static async Task SeedAsync(IServiceProvider services, Uri redirectUri, string clientSecret)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var logger = services.GetRequiredService<ILoggerFactory>()
                             .CreateLogger(typeof(AspireDashboardSeeder));

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = "aspire-dashboard",
            ClientType = OpenIddictConstants.ClientTypes.Confidential,
            ClientSecret = clientSecret,
            DisplayName = "Aspire Dashboard",
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

        // Dashboard's forwarded-headers handling behind Caddy is unreliable, so its scheme is unpredictable.
        descriptor.RedirectUris.Add(redirectUri);
        descriptor.RedirectUris.Add(WithAlternateScheme(redirectUri));

        var existing = await manager.FindByClientIdAsync("aspire-dashboard");
        if (existing is null)
        {
            await manager.CreateAsync(descriptor);
            logger.LogInformation("Created OpenIddict client 'aspire-dashboard' with redirect URIs {RedirectUris}", descriptor.RedirectUris);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor);
            logger.LogInformation("Updated OpenIddict client 'aspire-dashboard' with redirect URIs {RedirectUris}", descriptor.RedirectUris);
        }
    }

    private static Uri WithAlternateScheme(Uri uri)
    {
        var alternateScheme = uri.Scheme == Uri.UriSchemeHttps ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        return new Uri($"{alternateScheme}://{uri.Authority}{uri.PathAndQuery}");
    }
}
