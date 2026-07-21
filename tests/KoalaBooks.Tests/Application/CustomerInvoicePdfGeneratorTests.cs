using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using QuestPDF.Infrastructure;

namespace KoalaBooks.Tests.Application;

public class CustomerInvoicePdfGeneratorTests
{
    static CustomerInvoicePdfGeneratorTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    [Fact]
    public void Generate_ProducesNonEmptyPdfBytes()
    {
        var invoice = new CustomerInvoice
        {
            InvoiceNumber = 42,
            CustomerName = "Acme AB",
            InvoiceDate = new DateOnly(2026, 7, 1),
            DueDate = new DateOnly(2026, 7, 31),
            Lines =
            [
                new CustomerInvoiceLine { Description = "Konsulttjänst", Quantity = 1, UnitPrice = 1000, VatRate = 25, AmountExclVat = 1000, VatAmount = 250, TotalAmount = 1250 }
            ],
            AmountExclVat = 1000,
            VatAmount = 250,
            TotalAmount = 1250
        };

        var bytes = CustomerInvoicePdfGenerator.Generate(invoice);

        Assert.NotEmpty(bytes);
        // PDF file magic number.
        Assert.Equal("%PDF"u8.ToArray(), bytes.Take(4).ToArray());
    }
}
