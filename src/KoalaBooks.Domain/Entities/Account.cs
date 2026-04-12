namespace KoalaBooks.Domain.Entities;

public class Account
{
    public int Id { get; set; }
    public required string AccountNumber { get; set; }
    public required string Name { get; set; }
    public Enums.AccountClass AccountClass { get; set; }
    public bool IsActive { get; set; } = true;

    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;
}
