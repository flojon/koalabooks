using jsiSIE;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class SieImportServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly SieImportService _service;

    public SieImportServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _service = new SieImportService(_db);
    }

    public void Dispose() => _db.Dispose();

    private static Stream MakeSieStream(string content)
    {
        return new MemoryStream(System.Text.Encoding.Latin1.GetBytes(content));
    }

    private const string SampleSie4 = """
        #FLAGGA 0
        #FORMAT PC8
        #SIETYP 4
        #PROGRAM "TestApp" 1.0
        #GEN 20260101
        #FNAMN "Koala AB"
        #ORGNR 5591234567
        #RAR 0 20260101 20261231
        #KONTO 1910 "Kassa"
        #KONTO 1930 "Foretagskonto"
        #KONTO 3010 "Forsaljning"
        #KONTO 5010 "Lokalhyra"
        #VER "A" 1 20260115 "Hyra januari"
        {
            #TRANS 5010 {} 10000.00 20260115 "Hyra"
            #TRANS 1930 {} -10000.00 20260115 "Hyra"
        }
        #VER "A" 2 20260201 "Kundbetalning"
        {
            #TRANS 1930 {} 25000.00 20260201 ""
            #TRANS 3010 {} -25000.00 20260201 ""
        }
        """;

    [Fact]
    public void Parse_ValidSie4_ReturnsSieDocument()
    {
        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);

        Assert.Equal(4, doc.SIETYP);
        Assert.Equal("Koala AB", doc.FNAMN?.Name);
        Assert.True(doc.KONTO.Count >= 4);
        Assert.Equal(2, doc.VER.Count);
    }

    [Fact]
    public async Task GetPreview_ShowsFiscalYearsAndCounts()
    {
        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);
        var preview = await _service.GetPreviewAsync(doc);

        Assert.Equal("Koala AB", preview.CompanyName);
        Assert.Equal(4, preview.SieType);
        Assert.True(preview.AccountCount >= 4);
        Assert.Equal(2, preview.VoucherCount);
        Assert.Single(preview.FiscalYears);

        var fy = preview.FiscalYears[0];
        Assert.Equal(new DateOnly(2026, 1, 1), fy.Start);
        Assert.Equal(new DateOnly(2026, 12, 31), fy.End);
        Assert.False(fy.ExistsInDatabase);
    }

    [Fact]
    public async Task ImportFiscalYear_CreatesNewYear()
    {
        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);
        var result = await _service.ImportFiscalYearAsync(doc, 0, overwrite: false);

        Assert.Equal("2026", result.FiscalYearName);
        Assert.Equal(2, result.EntriesImported);
        Assert.Equal(4, result.LinesImported);
        Assert.True(result.AccountsCreated >= 4);

        var fiscalYear = await _db.FiscalYears.SingleAsync();
        Assert.Equal(new DateOnly(2026, 1, 1), fiscalYear.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), fiscalYear.EndDate);

        var entries = await _db.JournalEntries
            .Include(j => j.Lines)
            .OrderBy(j => j.EntryNumber)
            .ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].EntryNumber);
        Assert.Equal(2, entries[1].EntryNumber);
    }

    [Fact]
    public async Task ImportFiscalYear_MapsAmountsCorrectly()
    {
        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);
        await _service.ImportFiscalYearAsync(doc, 0, overwrite: false);

        var entry = await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .FirstAsync(j => j.EntryNumber == 1);

        var debitLine = entry.Lines.Single(l => l.DebitAmount > 0);
        var creditLine = entry.Lines.Single(l => l.CreditAmount > 0);

        Assert.Equal(10000m, debitLine.DebitAmount);
        Assert.Equal(0m, debitLine.CreditAmount);
        Assert.Equal("5010", debitLine.Account.AccountNumber);

        Assert.Equal(0m, creditLine.DebitAmount);
        Assert.Equal(10000m, creditLine.CreditAmount);
        Assert.Equal("1930", creditLine.Account.AccountNumber);
    }

    [Fact]
    public async Task ImportFiscalYear_OverwriteDeletesExistingEntries()
    {
        // First import
        using var stream1 = MakeSieStream(SampleSie4);
        var doc1 = _service.Parse(stream1);
        await _service.ImportFiscalYearAsync(doc1, 0, overwrite: false);

        Assert.Equal(2, await _db.JournalEntries.CountAsync());

        // Second import with overwrite
        using var stream2 = MakeSieStream(SampleSie4);
        var doc2 = _service.Parse(stream2);
        var result = await _service.ImportFiscalYearAsync(doc2, 0, overwrite: true);

        Assert.Equal(2, result.EntriesImported);
        // Should still be exactly 2 entries (old deleted, new imported)
        Assert.Equal(2, await _db.JournalEntries.CountAsync());
        // Still exactly 1 fiscal year
        Assert.Single(await _db.FiscalYears.ToListAsync());
    }

    [Fact]
    public async Task ImportFiscalYear_WithoutOverwrite_ThrowsIfExists()
    {
        using var stream1 = MakeSieStream(SampleSie4);
        var doc1 = _service.Parse(stream1);
        await _service.ImportFiscalYearAsync(doc1, 0, overwrite: false);

        using var stream2 = MakeSieStream(SampleSie4);
        var doc2 = _service.Parse(stream2);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ImportFiscalYearAsync(doc2, 0, overwrite: false));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task ImportFiscalYear_UpsertsAccounts()
    {
        // Pre-create the fiscal year matching the SIE file's dates
        var fy = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(fy);
        await _db.SaveChangesAsync();

        // Pre-existing account with different name in that fiscal year
        _db.Accounts.Add(new Account
        {
            AccountNumber = "1910",
            Name = "Old Name",
            AccountClass = AccountClass.Asset,
            FiscalYearId = fy.Id
        });
        await _db.SaveChangesAsync();

        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);
        // Overwrite=true since the FY already exists
        var result = await _service.ImportFiscalYearAsync(doc, 0, overwrite: true);

        // Overwrite deletes existing accounts and re-creates them all
        Assert.True(result.AccountsCreated >= 4);

        var updatedAccount = await _db.Accounts.SingleAsync(a => a.AccountNumber == "1910" && a.FiscalYearId == fy.Id);
        Assert.Equal("Kassa", updatedAccount.Name);
    }

    [Fact]
    public async Task GetPreview_DetectsExistingFiscalYear()
    {
        _db.FiscalYears.Add(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });
        await _db.SaveChangesAsync();

        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);
        var preview = await _service.GetPreviewAsync(doc);

        Assert.Single(preview.FiscalYears);
        Assert.True(preview.FiscalYears[0].ExistsInDatabase);
        Assert.NotNull(preview.FiscalYears[0].ExistingFiscalYearId);
    }
}
