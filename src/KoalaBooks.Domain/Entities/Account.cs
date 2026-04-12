namespace KoalaBooks.Domain.Entities;

public class Account
{
    public int Id { get; set; }
    public required string AccountNumber { get; set; }
    public required string Name { get; set; }
    public Enums.AccountClass AccountClass { get; set; }
    public bool IsActive { get; set; } = true;
}
