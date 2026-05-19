using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Domain;

public class LocalCurrentUser : ICurrentUser
{
    public int? OrganisationId { get; set; }

    public LocalCurrentUser(int? organisationId = null)
    {
        OrganisationId = organisationId;
    }
}
