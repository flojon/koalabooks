namespace KoalaBooks.Web.Models.Api;

public record CustomerInvoiceResponse(
    int Id,
    int FiscalYearId,
    int? CustomerId,
    string CustomerName,
    string? CustomerOrgNumber,
    string? CustomerAddress,
    string? CustomerPostalCode,
    string? CustomerCity,
    int InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string? OurReference,
    string? YourReference,
    string? Notes,
    List<CustomerInvoiceLineResponse> Lines,
    decimal AmountExclVat,
    decimal VatAmount,
    decimal TotalAmount,
    bool IsPosted,
    bool IsPaid,
    DateOnly? PaidDate,
    int? JournalEntryId,
    int? PaymentJournalEntryId,
    DateTime CreatedAt);

public record CustomerInvoiceLineResponse(
    int Id,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    int VatRate,
    decimal AmountExclVat,
    decimal VatAmount,
    decimal TotalAmount);
