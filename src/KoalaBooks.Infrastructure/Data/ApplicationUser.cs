using Microsoft.AspNetCore.Identity;

namespace KoalaBooks.Infrastructure.Data;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
}
