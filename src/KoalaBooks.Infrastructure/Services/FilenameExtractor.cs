using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Infrastructure.Services;

public class FilenameExtractor : IDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        string? type = null;
        if (name.Contains("kundfaktura") || name.Contains("customer"))
            type = "CustomerInvoice";
        else if (name.Contains("faktura") || name.Contains("invoice") || name.Contains("fakt"))
            type = "SupplierInvoice";
        else if (name.Contains("kvitto") || name.Contains("receipt"))
            type = "JournalEntry";

        return Task.FromResult(new ExtractionResult(type, null, null, null, null, null, null));
    }
}
