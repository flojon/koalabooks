using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class MarkSupplierInvoicePaidRequest
{
    [Required]
    public DateOnly? PaidDate { get; init; }

    [Required]
    public int? BankAccountId { get; init; }

    [Required]
    public int? PayableAccountId { get; init; }

    public int? LinkBankTransactionId { get; init; }
}
