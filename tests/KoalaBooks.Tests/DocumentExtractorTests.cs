using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

public class DocumentExtractorTests
{
    private readonly FilenameExtractor _filename = new();
    private readonly PdfTextExtractor _pdf = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfTextExtractor>.Instance);

    // ── FilenameExtractor ────────────────────────────────────────────

    [Theory]
    [InlineData("kundfaktura-2024.pdf", "CustomerInvoice")]
    [InlineData("customer-invoice.pdf", "CustomerInvoice")]
    [InlineData("faktura-leverantör.pdf", "SupplierInvoice")]
    [InlineData("invoice_2024.pdf", "SupplierInvoice")]
    [InlineData("fakt123.pdf", "SupplierInvoice")]
    [InlineData("kvitto-jan.jpg", "JournalEntry")]
    [InlineData("receipt_2024.jpg", "JournalEntry")]
    [InlineData("bankutdrag.pdf", null)]
    public async Task FilenameExtractor_DetectsType(string fileName, string? expectedType)
    {
        var result = await _filename.ExtractAsync(fileName, "application/pdf", []);
        Assert.Equal(expectedType, result.SuggestedType);
    }

    [Fact]
    public async Task FilenameExtractor_CustomerBeforeSupplier()
    {
        // "kundfaktura" contains "faktura" — must match CustomerInvoice not SupplierInvoice
        var result = await _filename.ExtractAsync("kundfaktura-mars.pdf", "application/pdf", []);
        Assert.Equal("CustomerInvoice", result.SuggestedType);
    }

    [Fact]
    public async Task FilenameExtractor_ReturnsNullAmountsAndDates()
    {
        var result = await _filename.ExtractAsync("faktura.pdf", "application/pdf", []);
        Assert.Null(result.Amount);
        Assert.Null(result.InvoiceDate);
        Assert.Null(result.DueDate);
        Assert.Null(result.InvoiceNumber);
        Assert.Null(result.Supplier);
    }

    // ── PdfTextExtractor ────────────────────────────────────────────

    [Fact]
    public async Task PdfTextExtractor_SkipsNonPdf()
    {
        var result = await _pdf.ExtractAsync("foto.jpg", "image/jpeg", [1, 2, 3]);
        Assert.Null(result.SuggestedType);
        Assert.Null(result.Amount);
    }

    [Fact]
    public async Task PdfTextExtractor_ReturnsNullsOnCorruptData()
    {
        // Corrupt PDF bytes — should not throw, just return empty result
        var result = await _pdf.ExtractAsync("bad.pdf", "application/pdf", [0xFF, 0xFE, 0x00]);
        Assert.Null(result.SuggestedType);
    }

    // ── CompositeExtractor ───────────────────────────────────────────

    [Fact]
    public async Task CompositeExtractor_PdfTypeTakesPriority()
    {
        var composite = new CompositeExtractor(_filename, _pdf);
        var result = await composite.ExtractAsync("faktura.jpg", "image/jpeg", []);
        // PDF returns null (not a pdf), filename returns SupplierInvoice
        Assert.Equal("SupplierInvoice", result.SuggestedType);
    }

    [Fact]
    public async Task CompositeExtractor_FallsBackToFilenameWhenPdfFindsNothing()
    {
        var composite = new CompositeExtractor(_filename, _pdf);
        // Non-PDF image with "kvitto" in filename → JournalEntry via filename fallback
        var result = await composite.ExtractAsync("kvitto-jan.jpg", "image/jpeg", []);
        Assert.Equal("JournalEntry", result.SuggestedType);
    }
}
