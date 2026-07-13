namespace KoalaBooks.Domain.Entities;

public class VoucherGapExplanation
{
    public int Id { get; set; }
    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;
    public int MissingEntryNumber { get; set; }
    public required string Explanation { get; set; }
    public DateTime ExplainedAt { get; set; } = DateTime.UtcNow;
    public required string ExplainedBy { get; set; }
}
