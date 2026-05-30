using KoalaBooks.Domain;

namespace KoalaBooks.Tests;

public class CurrentUserTests
{
    [Fact]
    public void LocalCurrentUser_DefaultConstructor_ReturnsNullOrgId()
    {
        var user = new LocalCurrentUser();
        Assert.Null(user.OrganisationId);
    }

    [Fact]
    public void LocalCurrentUser_WithOrgId_ReturnsOrgId()
    {
        var user = new LocalCurrentUser(42);
        Assert.Equal(42, user.OrganisationId);
    }

    [Fact]
    public void LocalCurrentUser_OrgIdIsMutable()
    {
        var user = new LocalCurrentUser();
        user.OrganisationId = 7;
        Assert.Equal(7, user.OrganisationId);
    }
}
