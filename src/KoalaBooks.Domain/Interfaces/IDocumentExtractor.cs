namespace KoalaBooks.Domain.Interfaces;

public record ExtractionResult(
    string? SuggestedType,
    string? Supplier,
    decimal? Amount,
    decimal? VatAmount,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string? InvoiceNumber
);

public interface IDocumentExtractor
{
    Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data);
}
