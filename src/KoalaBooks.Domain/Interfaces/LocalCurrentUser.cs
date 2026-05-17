namespace KoalaBooks.Domain.Interfaces;

public class LocalCurrentUser : ICurrentUser
{
    public int? OrganisationId { get; set; }

    public LocalCurrentUser(int? organisationId = null)
    {
        OrganisationId = organisationId;
    }
}
