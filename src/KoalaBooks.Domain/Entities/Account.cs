namespace KoalaBooks.Domain.Entities;

public class Account
{
    public int Id { get; set; }
    public required string AccountNumber { get; set; }
    public required string Name { get; set; }
    public Enums.AccountClass AccountClass { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Incoming balance (IB / Ingående balans) from SIE import.</summary>
    public decimal IncomingBalance { get; set; }

    /// <summary>Outgoing balance (UB / Utgående balans) from SIE import.</summary>
    public decimal OutgoingBalance { get; set; }

    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;
}
