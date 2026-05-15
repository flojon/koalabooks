using KoalaBooks.Domain.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace KoalaBooks.Web.Services;

public static class CustomerInvoicePdfGenerator
{
    public static byte[] Generate(CustomerInvoice invoice)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var orgName = invoice.FiscalYear?.Organisation?.Name ?? "KoalaBooks";

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Arial));

                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(orgName).Bold().FontSize(18);
                    });
                    row.ConstantItem(160).AlignRight().Column(col =>
                    {
                        col.Item().Text("FAKTURA").Bold().FontSize(16);
                        col.Item().Text($"Nr: {invoice.InvoiceNumber}").FontSize(11);
                    });
                });

                page.Content().PaddingTop(20).Column(col =>
                {
                    // Customer block
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("Kund").Bold();
                            c.Item().Text(invoice.CustomerName);
                            if (invoice.Customer?.Address is not null)
                                c.Item().Text(invoice.Customer.Address);
                            if (invoice.Customer?.PostalCode is not null || invoice.Customer?.City is not null)
                                c.Item().Text($"{invoice.Customer?.PostalCode} {invoice.Customer?.City}".Trim());
                        });

                        row.ConstantItem(200).AlignRight().Column(c =>
                        {
                            c.Item().Row(r =>
                            {
                                r.ConstantItem(90).Text("Fakturadatum:").Bold();
                                r.RelativeItem().Text(invoice.InvoiceDate.ToString("yyyy-MM-dd"));
                            });
                            c.Item().Row(r =>
                            {
                                r.ConstantItem(90).Text("Förfallodatum:").Bold();
                                r.RelativeItem().Text(invoice.DueDate.ToString("yyyy-MM-dd"));
                            });
                            if (invoice.OurReference is not null)
                                c.Item().Row(r =>
                                {
                                    r.ConstantItem(90).Text("Vår ref:").Bold();
                                    r.RelativeItem().Text(invoice.OurReference);
                                });
                            if (invoice.YourReference is not null)
                                c.Item().Row(r =>
                                {
                                    r.ConstantItem(90).Text("Er ref:").Bold();
                                    r.RelativeItem().Text(invoice.YourReference);
                                });
                        });
                    });

                    col.Item().PaddingTop(20).LineHorizontal(1);

                    // Lines table
                    col.Item().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            cols.RelativeColumn(5);   // Description
                            cols.RelativeColumn(1.5f); // Qty
                            cols.RelativeColumn(2);    // Unit price
                            cols.RelativeColumn(1);    // VAT %
                            cols.RelativeColumn(2);    // Total excl.
                            cols.RelativeColumn(2);    // Total incl.
                        });

                        // Header
                        static IContainer HeaderCell(IContainer c) =>
                            c.DefaultTextStyle(t => t.Bold()).PaddingVertical(4).PaddingHorizontal(2)
                             .BorderBottom(1);

                        table.Header(header =>
                        {
                            header.Cell().Element(HeaderCell).Text("Beskrivning");
                            header.Cell().Element(HeaderCell).AlignRight().Text("Antal");
                            header.Cell().Element(HeaderCell).AlignRight().Text("À-pris");
                            header.Cell().Element(HeaderCell).AlignRight().Text("Moms %");
                            header.Cell().Element(HeaderCell).AlignRight().Text("Exkl. moms");
                            header.Cell().Element(HeaderCell).AlignRight().Text("Inkl. moms");
                        });

                        static IContainer DataCell(IContainer c) =>
                            c.PaddingVertical(3).PaddingHorizontal(2);

                        foreach (var line in invoice.Lines)
                        {
                            table.Cell().Element(DataCell).Text(line.Description);
                            table.Cell().Element(DataCell).AlignRight().Text(line.Quantity.ToString("G29"));
                            table.Cell().Element(DataCell).AlignRight().Text(line.UnitPrice.ToString("N2"));
                            table.Cell().Element(DataCell).AlignRight().Text($"{line.VatRate} %");
                            table.Cell().Element(DataCell).AlignRight().Text(line.AmountExclVat.ToString("N2"));
                            table.Cell().Element(DataCell).AlignRight().Text(line.TotalAmount.ToString("N2"));
                        }
                    });

                    col.Item().PaddingTop(6).LineHorizontal(1);

                    // Totals
                    col.Item().PaddingTop(6).AlignRight().Column(c =>
                    {
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(120).Text("Summa exkl. moms:").Bold();
                            r.ConstantItem(80).AlignRight().Text(invoice.AmountExclVat.ToString("N2"));
                        });
                        c.Item().Row(r =>
                        {
                            r.ConstantItem(120).Text("Moms:").Bold();
                            r.ConstantItem(80).AlignRight().Text(invoice.VatAmount.ToString("N2"));
                        });
                        c.Item().PaddingTop(4).Row(r =>
                        {
                            r.ConstantItem(120).Text("ATT BETALA:").Bold().FontSize(12);
                            r.ConstantItem(80).AlignRight().Text(invoice.TotalAmount.ToString("N2")).Bold().FontSize(12);
                        });
                    });

                    if (invoice.Notes is not null)
                    {
                        col.Item().PaddingTop(20).Text("Anteckningar:").Bold();
                        col.Item().Text(invoice.Notes);
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Sida ");
                    t.CurrentPageNumber();
                    t.Span(" av ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
    }
}
