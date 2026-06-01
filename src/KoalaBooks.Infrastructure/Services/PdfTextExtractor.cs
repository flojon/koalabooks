using KoalaBooks.Domain.Interfaces;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace KoalaBooks.Infrastructure.Services;

public partial class PdfTextExtractor : IDocumentExtractor
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
        catch
        {
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

    private static ExtractionResult Parse(string text)
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
        if (Regex.IsMatch(text, @"[Kk]undfaktura|[Ss]ales [Ii]nvoice", RegexOptions.IgnoreCase))
            return "CustomerInvoice";
        if (Regex.IsMatch(text, @"[Ff]aktura|[Ii]nvoice", RegexOptions.IgnoreCase))
            return "SupplierInvoice";
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
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractSupplier(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"\b(AB|KB|HB|Inc|Ltd|GmbH|AS)\b"))
                return trimmed.Length > 100 ? null : trimmed;
        }
        return null;
    }

    [GeneratedRegex(@"([\d\s]+[,.][\d]{2})\s*(kr|SEK)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"[Ff]akturadatum[:\s]+(\d{4}-\d{2}-\d{2})")]
    private static partial Regex InvoiceDatePattern();

    [GeneratedRegex(@"[Ff]örfallodatum[:\s]+(\d{4}-\d{2}-\d{2})")]
    private static partial Regex DueDatePattern();

    [GeneratedRegex(@"[Ff]akturanummer[:\s]+(\S+)|[Ii]nvoice\s+[Nn]o[:\s]+(\S+)")]
    private static partial Regex InvoiceNumberPattern();
}
