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

    // ── PdfTextExtractor.Parse (real-world PdfPig text fixtures) ─────
    //
    // PdfPig concatenates separately-positioned text runs with no space when the
    // source PDF doesn't encode an explicit space glyph between them (e.g. a
    // "FAKTURA" heading immediately followed by a "Datum:" field label). These
    // fixtures are verbatim excerpts of what PdfTextExtractor.ExtractText actually
    // produces for real invoices — not hand-wrapped/idealized text — since that
    // concatenation is exactly what broke date extraction.

    [Fact]
    public void Parse_ExtractsDateFromAllCapsHeadingGluedToLabel()
    {
        // DNB invoice: "FAKTURA" heading (all caps) runs directly into "Datum:" with
        // no separator, so "FAKTURADatum" fails a pattern that only lowercases the
        // first letter of "fakturadatum".
        const string text = "1(1)FAKTURADatum:2026-03-0510588STOCKHOLMFlodénConsultingAB";
        var result = PdfTextExtractor.Parse(text);
        Assert.Equal(new DateOnly(2026, 3, 5), result.InvoiceDate);
    }

    [Fact]
    public void Parse_ExtractsDateFromPlainDatumLabelWithNoSeparator()
    {
        // inet.se receipt: label is just "Datum" (no "Faktura" prefix) and butts
        // directly against the date with no colon or space at all.
        const string text = "KVITTO888523495Kundnummer1765968Datum2026-02-18Orderdatum2026-02-18";
        var result = PdfTextExtractor.Parse(text);
        Assert.Equal(new DateOnly(2026, 2, 18), result.InvoiceDate);
    }

    [Fact]
    public void Parse_DistinguishesInvoiceDateFromDueDate()
    {
        // Inleed invoice — regression guard: broadening the invoice-date pattern to
        // match any "...datum" label must not make it swallow Förfallodatum too.
        const string text = "Faktura #476732OCR-nummer:2072036885223563Fakturadatum:2026-05-25Förfallodatum:2026-06-04Status:Betald";
        var result = PdfTextExtractor.Parse(text);
        Assert.Equal(new DateOnly(2026, 5, 25), result.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 6, 4), result.DueDate);
    }

    [Fact]
    public void Parse_DoesNotPickUpDueDateWhenItAppearsBeforeInvoiceDateInText()
    {
        // PdfPig's extraction order follows the PDF's internal text-run order, which
        // doesn't always match human reading order — a layout could surface
        // Förfallodatum before Fakturadatum. The broadened "datum" pattern must not
        // mistake the due date for the invoice date regardless of which comes first.
        const string text = "SammanfattningFörfallodatum:2026-06-04ÖvrigtFakturadatum:2026-05-25Belopp";
        var result = PdfTextExtractor.Parse(text);
        Assert.Equal(new DateOnly(2026, 5, 25), result.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 6, 4), result.DueDate);
    }

    [Fact]
    public void Parse_DetectsReceiptTypeFromKvittoInText()
    {
        // ok-q8 receipt: PDF text says "Kvitto" but never "faktura"/"invoice", so
        // DetectType returned null even though the filename fallback (FilenameExtractor)
        // isn't available here since the real filename doesn't contain "kvitto" either.
        const string text = "Kvitto för din hyraMellan SUV ElbilFXS56LSammanfattningHyresavgift";
        var result = PdfTextExtractor.Parse(text);
        Assert.Equal("JournalEntry", result.SuggestedType);
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
