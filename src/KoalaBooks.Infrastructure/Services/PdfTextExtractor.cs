using KoalaBooks.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace KoalaBooks.Infrastructure.Services;

public partial class PdfTextExtractor(ILogger<PdfTextExtractor> logger) : IDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        if (contentType != "application/pdf")
            return Task.FromResult(new ExtractionResult(null, null, null, null, null, null, null));

        try
        {
            var text = ExtractText(data);
            return Task.FromResult(Parse(text));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PDF text extraction failed for {FileName}", fileName);
            return Task.FromResult(new ExtractionResult(null, null, null, null, null, null, null));
        }
    }

    private static string ExtractText(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    internal static ExtractionResult Parse(string text)
    {
        var type = DetectType(text);
        var amount = ExtractAmount(text);
        var invoiceDate = ExtractDate(text, InvoiceDatePattern());
        var dueDate = ExtractDate(text, DueDatePattern());
        var invoiceNumber = ExtractInvoiceNumber(text);
        var supplier = ExtractSupplier(text);

        return new ExtractionResult(type, supplier, amount?.excl, amount?.vat, invoiceDate, dueDate, invoiceNumber);
    }

    private static string? DetectType(string text)
    {
        if (Regex.IsMatch(text, @"kundfaktura|sales invoice", RegexOptions.IgnoreCase))
            return "CustomerInvoice";
        if (Regex.IsMatch(text, @"faktura|invoice", RegexOptions.IgnoreCase))
            return "SupplierInvoice";
        if (Regex.IsMatch(text, @"kvitto|receipt", RegexOptions.IgnoreCase))
            return "JournalEntry";
        return null;
    }

    private static (decimal excl, decimal? vat)? ExtractAmount(string text)
    {
        var match = AmountPattern().Match(text);
        if (!match.Success) return null;
        var raw = match.Groups[1].Value.Replace(" ", "").Replace(",", ".");
        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return null;
        return (amount, null);
    }

    private static DateOnly? ExtractDate(string text, Regex pattern)
    {
        var match = pattern.Match(text);
        if (!match.Success) return null;
        return DateOnly.TryParse(match.Groups[1].Value, out var date) ? date : null;
    }

    private static string? ExtractInvoiceNumber(string text)
    {
        var match = InvoiceNumberPattern().Match(text);
        if (!match.Success) return null;
        var value = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(value))
            value = match.Groups[2].Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? ExtractSupplier(string text)
    {
        // TODO: "AS" and "Inc" produce false positives on common English words; replace with
        // org-number heuristics or AI extraction when available.
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"\b(AB|KB|HB|Inc|Ltd|GmbH|AS)\b"))
                return trimmed.Length > 100 ? trimmed[..100] : trimmed;
        }
        return null;
    }

    [GeneratedRegex(@"([\d\s]+[,.][\d]{2})\s*(kr|SEK)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    // PdfPig often concatenates separately-positioned text runs with no space (e.g. an
    // all-caps "FAKTURA" heading glued directly to a "Datum:" label becomes
    // "FAKTURADatum:..."), and some layouts drop the "Faktura" prefix and/or the colon
    // entirely (e.g. "Datum2026-02-18"). Match on the bare "datum" suffix, case-
    // insensitively, with an optional separator — but never match it as part of
    // "förfallodatum" (due date), which DueDatePattern owns instead.
    [GeneratedRegex(@"(?<!förfallo)datum[:\s]*(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex InvoiceDatePattern();

    [GeneratedRegex(@"förfallodatum[:\s]*(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex DueDatePattern();

    [GeneratedRegex(@"[Ff]akturanummer[:\s]+(\S+)|[Ii]nvoice\s+[Nn]o[:\s]+(\S+)")]
    private static partial Regex InvoiceNumberPattern();
}
