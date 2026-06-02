namespace KoalaBooks.Domain.Entities;

public class CustomerInvoice
{
    public int Id { get; set; }
    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string CustomerName { get; set; } = "";
    public string? CustomerOrgNumber { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerPostalCode { get; set; }
    public string? CustomerCity { get; set; }

    public int InvoiceNumber { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }

    public string? OurReference { get; set; }
    public string? YourReference { get; set; }
    public string? Notes { get; set; }

    public List<CustomerInvoiceLine> Lines { get; set; } = [];

    public decimal AmountExclVat { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public bool IsPosted { get; set; }
    public bool IsPaid { get; set; }
    public DateOnly? PaidDate { get; set; }

    public int? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public int? PaymentJournalEntryId { get; set; }
    public JournalEntry? PaymentJournalEntry { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Document> Documents { get; set; } = [];
}
