using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class CreateCustomerInvoiceRequest
{
    public int? CustomerId { get; init; }

    [Required]
    public string CustomerName { get; init; } = "";

    [Required]
    public DateOnly? InvoiceDate { get; init; }

    [Required]
    public DateOnly? DueDate { get; init; }

    public string? OurReference { get; init; }
    public string? YourReference { get; init; }
    public string? Notes { get; init; }

    [MinLength(1)]
    public List<CreateCustomerInvoiceLineRequest> Lines { get; init; } = [];
}

public class CreateCustomerInvoiceLineRequest
{
    [Required]
    public string Description { get; init; } = "";

    public decimal Quantity { get; init; } = 1;
    public decimal UnitPrice { get; init; }
    public int VatRate { get; init; }
}
