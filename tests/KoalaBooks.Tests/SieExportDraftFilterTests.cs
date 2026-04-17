using System.Text;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

/// <summary>
/// Regression tests for SIE export draft entry filtering.
/// Bug: SIE export may include draft (unposted) journal entries in the
/// voucher section, violating SIE-4 spec and producing incorrect exports.
/// Only posted entries should appear in #VER/#TRANS records.
/// </summary>
public class SieExportDraftFilterTests : IDisposable
{
    private readonly TestFixture _f;

    public SieExportDraftFilterTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    private static string DecodeExport(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437).GetString(bytes);
    }

    /// <summary>
    /// Draft entries must never appear in SIE export. Only posted entries
    /// should generate #VER records.
    /// </summary>
    [Fact]
    public async Task SieExport_ExcludesDraftEntries()
    {
        // Arrange
        var fy = _f.CreateFiscalYear();
        var cash = _f.CreateAccount(fy.Id, "1910", "Kassa");
        var revenue = _f.CreateAccount(fy.Id, "3010", "Försäljning", AccountClass.Revenue);

        // Create one posted entry
        await _f.CreateAndPostEntryAsync(fy.Id, cash.Id, revenue.Id, 5_000m,
            date: new DateOnly(2026, 3, 1), description: "Posted sale");

        // Create a draft entry (NOT posted)
        var draftEntry = _f.MakeEntry(fy.Id, cash.Id, revenue.Id, 9_999m,
            date: new DateOnly(2026, 4, 1), description: "Draft sale");
        await _f.JournalEntryService.CreateAsync(draftEntry);

        // Act
        var bytes = await _f.SieExportService.ExportAsync(fy.Id);
        var text = DecodeExport(bytes);

        // Assert: posted entry appears, draft does not
        Assert.Contains("Posted sale", text);
        Assert.DoesNotContain("Draft sale", text);

        // Only one #VER record
        var verCount = text.Split('\n').Count(l => l.TrimStart().StartsWith("#VER"));
        Assert.Equal(1, verCount);
    }

    /// <summary>
    /// Posted entries must appear in the SIE export with correct amounts.
    /// </summary>
    [Fact]
    public async Task SieExport_IncludesPostedEntries()
    {
        // Arrange
        var fy = _f.CreateFiscalYear();
        var cash = _f.CreateAccount(fy.Id, "1910", "Kassa");
        var expense = _f.CreateAccount(fy.Id, "5010", "Lokalhyra", AccountClass.Expense);

        await _f.CreateAndPostEntryAsync(fy.Id, expense.Id, cash.Id, 8_000m,
            date: new DateOnly(2026, 2, 15), description: "Hyra februari");
        await _f.CreateAndPostEntryAsync(fy.Id, expense.Id, cash.Id, 8_000m,
            date: new DateOnly(2026, 3, 15), description: "Hyra mars");

        // Act
        var bytes = await _f.SieExportService.ExportAsync(fy.Id);
        var text = DecodeExport(bytes);

        // Assert: both posted entries appear
        Assert.Contains("Hyra februari", text);
        Assert.Contains("Hyra mars", text);

        var verCount = text.Split('\n').Count(l => l.TrimStart().StartsWith("#VER"));
        Assert.Equal(2, verCount);

        // Verify amounts
        Assert.Contains("#TRANS 5010 {} 8000.00", text);
        Assert.Contains("#TRANS 1910 {} -8000.00", text);
    }
}
