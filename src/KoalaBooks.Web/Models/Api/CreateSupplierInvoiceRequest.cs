using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class CreateSupplierInvoiceRequest
{
    [Required]
    public string SupplierName { get; init; } = "";

    public string? InvoiceNumber { get; init; }

    [Required]
    public DateOnly? InvoiceDate { get; init; }

    [Required]
    public DateOnly? DueDate { get; init; }

    public decimal AmountExclVat { get; init; }

    public decimal VatAmount { get; init; }

    [Required]
    public decimal? TotalAmount { get; init; }

    public string? Notes { get; init; }
}
