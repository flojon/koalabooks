namespace KoalaBooks.Domain.Entities;

public class SupplierInvoice
{
    public int Id { get; set; }
    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;

    public string SupplierName { get; set; } = "";
    public string? InvoiceNumber { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }

    public decimal AmountExclVat { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public string? Notes { get; set; }

    public bool IsPaid { get; set; }
    public DateOnly? PaidDate { get; set; }

    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public int? PaymentJournalEntryId { get; set; }
    public JournalEntry? PaymentJournalEntry { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
