using KoalaBooks.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace KoalaBooks.Infrastructure.Data;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public int? OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }
}
