using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class PostSupplierInvoiceRequest
{
    [Required]
    public int? ExpenseAccountId { get; init; }

    [Required]
    public int? PayableAccountId { get; init; }

    public int? VatAccountId { get; init; }
}
