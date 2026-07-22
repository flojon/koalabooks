namespace KoalaBooks.Web.Models.Api;

public class PostCustomerInvoiceRequest
{
    public int ReceivableAccountId { get; init; }
    public int RevenueAccountId { get; init; }
    public Dictionary<int, int> VatRateAccountIds { get; init; } = new();
}
