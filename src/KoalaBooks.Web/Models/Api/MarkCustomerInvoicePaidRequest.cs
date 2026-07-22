using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class MarkCustomerInvoicePaidRequest
{
    [Required]
    public DateOnly? PaidDate { get; init; }

    public int BankAccountId { get; init; }
    public int ReceivableAccountId { get; init; }
    public int? LinkBankTransactionId { get; init; }
}
