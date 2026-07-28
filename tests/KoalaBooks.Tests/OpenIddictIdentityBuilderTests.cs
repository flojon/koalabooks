using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Web.Pages.Connect;
using OpenIddict.Abstractions;

namespace KoalaBooks.Tests;

public class OpenIddictIdentityBuilderTests
{
    [Fact]
    public void BuildPrincipal_UserWithOrganisation_SetsOrgIdOnAccessToken()
    {
        var user = new ApplicationUser
        {
            UserName = "test@koalabooks.test",
            Email = "test@koalabooks.test",
            DisplayName = "Test User",
            OrganisationId = 42
        };

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(user, "user-id-1", ["profile", "email"]);

        var orgClaim = principal.FindFirst("org_id");
        Assert.NotNull(orgClaim);
        Assert.Equal("42", orgClaim.Value);
        Assert.Contains(OpenIddictConstants.Destinations.AccessToken, orgClaim.GetDestinations());
    }

    [Fact]
    public void BuildPrincipal_UserWithoutOrganisation_DoesNotSetOrgId()
    {
        var user = new ApplicationUser
        {
            UserName = "test@koalabooks.test",
            Email = "test@koalabooks.test",
            DisplayName = "Test User",
            OrganisationId = null
        };

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(user, "user-id-1", ["profile"]);

        Assert.Null(principal.FindFirst("org_id"));
    }

    [Fact]
    public void BuildPrincipal_SetsSubjectEmailAndName()
    {
        var user = new ApplicationUser
        {
            UserName = "someone@koalabooks.test",
            Email = "someone@koalabooks.test",
            DisplayName = "Someone Person",
            OrganisationId = 7
        };

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(user, "user-id-2", ["profile", "email"]);

        Assert.Equal("user-id-2", principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value);
        Assert.Equal("someone@koalabooks.test", principal.FindFirst(OpenIddictConstants.Claims.Email)?.Value);
        Assert.Equal("Someone Person", principal.FindFirst(OpenIddictConstants.Claims.Name)?.Value);
    }

    [Fact]
    public void BuildPrincipal_SubjectGoesToIdentityToken()
    {
        // sub is OIDC-mandatory in the id_token; without this destination OpenIddict issues no
        // id_token at all, which silently breaks the WASM client's AddOidcAuthentication() flow.
        var user = new ApplicationUser
        {
            UserName = "someone@koalabooks.test",
            Email = "someone@koalabooks.test",
            DisplayName = "Someone Person",
        };

        var principal = OpenIddictIdentityBuilder.BuildPrincipal(user, "user-id-3", ["openid", "profile"]);

        var subjectClaim = principal.FindFirst(OpenIddictConstants.Claims.Subject);
        Assert.NotNull(subjectClaim);
        Assert.Contains(OpenIddictConstants.Destinations.IdentityToken, subjectClaim.GetDestinations());
        Assert.Contains(OpenIddictConstants.Destinations.AccessToken, subjectClaim.GetDestinations());
    }
}
