namespace KoalaBooks.Domain.Entities;

public class CustomerInvoiceLine
{
    public int Id { get; set; }
    public int CustomerInvoiceId { get; set; }
    public CustomerInvoice CustomerInvoice { get; set; } = null!;

    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public int VatRate { get; set; } // 0, 6, 12 or 25 (percent)

    public decimal AmountExclVat { get; set; } // = Quantity * UnitPrice
    public decimal VatAmount { get; set; }     // = AmountExclVat * VatRate / 100
    public decimal TotalAmount { get; set; }   // = AmountExclVat + VatAmount
}
