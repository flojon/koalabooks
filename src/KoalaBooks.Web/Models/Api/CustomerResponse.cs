namespace KoalaBooks.Web.Models.Api;

public record CustomerResponse(
    int Id,
    int OrganisationId,
    string Name,
    string? OrgNumber,
    string? Email,
    string? Phone,
    string? Address,
    string? PostalCode,
    string? City,
    string Country,
    bool IsActive,
    DateTime CreatedAt);
