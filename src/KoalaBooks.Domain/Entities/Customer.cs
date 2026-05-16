namespace KoalaBooks.Domain.Entities;

public class Customer
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation Organisation { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? OrgNumber { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string Country { get; set; } = "SE";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
