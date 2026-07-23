using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class UpdateOrganisationRequest
{
    [Required]
    public string Name { get; init; } = "";

    public string? OrgNumber { get; init; }
}
