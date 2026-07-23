using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class CreateCustomerRequest
{
    [Required]
    public string Name { get; init; } = "";

    public string? OrgNumber { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public string Country { get; init; } = "SE";
}
