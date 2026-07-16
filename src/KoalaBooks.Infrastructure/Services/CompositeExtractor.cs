using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Infrastructure.Services;

public class CompositeExtractor(FilenameExtractor filename, PdfTextExtractor pdf) : IDocumentExtractor
{
    public async Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        var f = await filename.ExtractAsync(fileName, contentType, data).ConfigureAwait(false);
        var p = await pdf.ExtractAsync(fileName, contentType, data).ConfigureAwait(false);

        // PDF fields take priority; fall back to filename for type if PDF found nothing
        return new ExtractionResult(
            SuggestedType: p.SuggestedType ?? f.SuggestedType,
            Supplier:      p.Supplier,
            Amount:        p.Amount,
            VatAmount:     p.VatAmount,
            InvoiceDate:   p.InvoiceDate,
            DueDate:       p.DueDate,
            InvoiceNumber: p.InvoiceNumber
        );
    }
}
