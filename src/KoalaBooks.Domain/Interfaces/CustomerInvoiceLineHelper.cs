using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public static class CustomerInvoiceLineHelper
{
    public static void RecalcLine(CustomerInvoiceLine line)
    {
        line.AmountExclVat = Math.Round(line.Quantity * line.UnitPrice, 2);
        line.VatAmount = Math.Round(line.AmountExclVat * line.VatRate / 100m, 2);
        line.TotalAmount = line.AmountExclVat + line.VatAmount;
    }
}
