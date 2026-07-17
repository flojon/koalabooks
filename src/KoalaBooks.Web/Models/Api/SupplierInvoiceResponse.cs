namespace KoalaBooks.Web.Models.Api;

public record SupplierInvoiceResponse(
    int Id,
    int FiscalYearId,
    string SupplierName,
    string? InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    decimal AmountExclVat,
    decimal VatAmount,
    decimal TotalAmount,
    string? Notes,
    bool IsPaid,
    DateOnly? PaidDate,
    int? JournalEntryId,
    int? PaymentJournalEntryId,
    DateTime CreatedAt);
