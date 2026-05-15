using jsiSIE;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class SieImportServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly SieImportService _service;

    public SieImportServiceTests()
    {
        _f = new TestFixture();
        _service = new SieImportService(_f.Db, _f.Tenant);
    }

    public void Dispose() => _f.Dispose();

    private static Stream MakeSieStream(string content)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return new MemoryStream(System.Text.Encoding.GetEncoding(437).GetBytes(content));
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
        #KONTO 1930 "Företagskonto"
        #KONTO 3010 "Försäljning"
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
    public void Parse_CP437Encoding_HandlesSwedishCharacters()
    {
        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);

        Assert.Equal("Företagskonto", doc.KONTO["1930"].Name);
        Assert.Equal("Försäljning", doc.KONTO["3010"].Name);
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

        var fiscalYear = await _f.Db.FiscalYears.SingleAsync();
        Assert.Equal(new DateOnly(2026, 1, 1), fiscalYear.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), fiscalYear.EndDate);

        var entries = await _f.Db.JournalEntries
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

        var entry = await _f.Db.JournalEntries
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

        Assert.Equal(2, await _f.Db.JournalEntries.CountAsync());

        // Second import with overwrite
        using var stream2 = MakeSieStream(SampleSie4);
        var doc2 = _service.Parse(stream2);
        var result = await _service.ImportFiscalYearAsync(doc2, 0, overwrite: true);

        Assert.Equal(2, result.EntriesImported);
        // Should still be exactly 2 entries (old deleted, new imported)
        Assert.Equal(2, await _f.Db.JournalEntries.CountAsync());
        // Still exactly 1 fiscal year
        Assert.Single(await _f.Db.FiscalYears.ToListAsync());
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
            EndDate = new DateOnly(2026, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.Add(fy);
        await _f.Db.SaveChangesAsync();

        // Pre-existing account with different name in that fiscal year
        _f.Db.Accounts.Add(new Account
        {
            AccountNumber = "1910",
            Name = "Old Name",
            AccountClass = AccountClass.Asset,
            FiscalYearId = fy.Id
        });
        await _f.Db.SaveChangesAsync();

        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);
        // Overwrite=true since the FY already exists
        var result = await _service.ImportFiscalYearAsync(doc, 0, overwrite: true);

        // Overwrite deletes existing accounts and re-creates them all
        Assert.True(result.AccountsCreated >= 4);

        var updatedAccount = await _f.Db.Accounts.SingleAsync(a => a.AccountNumber == "1910" && a.FiscalYearId == fy.Id);
        Assert.Equal("Kassa", updatedAccount.Name);
    }

    [Fact]
    public async Task GetPreview_DetectsExistingFiscalYear()
    {
        _f.Db.FiscalYears.Add(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            OrganisationId = _f.TestOrgId
        });
        await _f.Db.SaveChangesAsync();

        using var stream = MakeSieStream(SampleSie4);
        var doc = _service.Parse(stream);
        var preview = await _service.GetPreviewAsync(doc);

        Assert.Single(preview.FiscalYears);
        Assert.True(preview.FiscalYears[0].ExistsInDatabase);
        Assert.NotNull(preview.FiscalYears[0].ExistingFiscalYearId);
    }

    private const string SampleSie4WithBalances = """
        #FLAGGA 0
        #FORMAT PC8
        #SIETYP 4
        #PROGRAM "TestApp" 1.0
        #GEN 20260101
        #FNAMN "Koala AB"
        #ORGNR 5591234567
        #RAR 0 20260101 20261231
        #RAR -1 20250101 20251231
        #KONTO 1910 "Kassa"
        #KONTO 1930 "Företagskonto"
        #KONTO 3010 "Försäljning"
        #KONTO 5010 "Lokalhyra"
        #IB 0 1910 50000.00
        #IB 0 1930 120000.00
        #UB 0 1910 75000.00
        #UB 0 1930 135000.00
        #IB -1 1910 30000.00
        #UB -1 1910 50000.00
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
    public async Task GetPreview_ShowsBalanceCount()
    {
        using var stream = MakeSieStream(SampleSie4WithBalances);
        var doc = _service.Parse(stream);
        var preview = await _service.GetPreviewAsync(doc);

        // Year 0 has 2 vouchers + 4 balances (2 IB + 2 UB)
        var fy0 = preview.FiscalYears.Single(f => f.RarId == 0);
        Assert.Equal(2, fy0.VoucherCount);
        Assert.Equal(4, fy0.BalanceCount);

        // Year -1 has 0 vouchers but 2 balances (1 IB + 1 UB)
        var fyPrev = preview.FiscalYears.Single(f => f.RarId == -1);
        Assert.Equal(0, fyPrev.VoucherCount);
        Assert.Equal(2, fyPrev.BalanceCount);
    }

    [Fact]
    public async Task ImportFiscalYear_ImportsIBUBBalances()
    {
        using var stream = MakeSieStream(SampleSie4WithBalances);
        var doc = _service.Parse(stream);
        var result = await _service.ImportFiscalYearAsync(doc, 0, overwrite: false);

        Assert.Equal(4, result.BalancesImported); // 2 IB + 2 UB

        var accounts = await _f.Db.Accounts.OrderBy(a => a.AccountNumber).ToListAsync();
        var kassa = accounts.Single(a => a.AccountNumber == "1910");
        Assert.Equal(50000m, kassa.IncomingBalance);
        Assert.Equal(75000m, kassa.OutgoingBalance);

        var foretag = accounts.Single(a => a.AccountNumber == "1930");
        Assert.Equal(120000m, foretag.IncomingBalance);
        Assert.Equal(135000m, foretag.OutgoingBalance);
    }

    [Fact]
    public async Task ImportFiscalYear_ImportsPreviousYearBalances()
    {
        using var stream = MakeSieStream(SampleSie4WithBalances);
        var doc = _service.Parse(stream);

        // Import previous year (RAR -1) — has only balances, no vouchers
        var result = await _service.ImportFiscalYearAsync(doc, -1, overwrite: false);

        Assert.Equal(0, result.EntriesImported);
        Assert.Equal(2, result.BalancesImported); // 1 IB + 1 UB

        var kassa = await _f.Db.Accounts.SingleAsync(a => a.AccountNumber == "1910");
        Assert.Equal(30000m, kassa.IncomingBalance);
        Assert.Equal(50000m, kassa.OutgoingBalance);
    }

    [Fact]
    public async Task ImportFiscalYear_OverwriteClearsBalances()
    {
        using var stream1 = MakeSieStream(SampleSie4WithBalances);
        var doc1 = _service.Parse(stream1);
        await _service.ImportFiscalYearAsync(doc1, 0, overwrite: false);

        // Verify balances exist
        var kassa = await _f.Db.Accounts.SingleAsync(a => a.AccountNumber == "1910");
        Assert.Equal(50000m, kassa.IncomingBalance);

        // Overwrite with basic sample (no balances)
        using var stream2 = MakeSieStream(SampleSie4);
        var doc2 = _service.Parse(stream2);
        await _service.ImportFiscalYearAsync(doc2, 0, overwrite: true);

        // Balances should be 0 (reset)
        kassa = await _f.Db.Accounts.SingleAsync(a => a.AccountNumber == "1910");
        Assert.Equal(0m, kassa.IncomingBalance);
        Assert.Equal(0m, kassa.OutgoingBalance);
    }

    [Fact]
    public async Task ImportAll_ImportsAllFiscalYears()
    {
        using var stream = MakeSieStream(SampleSie4WithBalances);
        var doc = _service.Parse(stream);
        var result = await _service.ImportAllAsync(doc, overwrite: false);

        // Should import 2 fiscal years (2025 with balances only, 2026 with vouchers + balances)
        Assert.Equal(2, result.FiscalYears.Count);
        Assert.Equal(2, result.TotalEntriesImported);
        Assert.True(result.TotalBalancesImported > 0);
        Assert.True(result.TotalAccountsCreated >= 4);

        // Both FYs should exist in DB
        Assert.Equal(2, await _f.Db.FiscalYears.CountAsync());
    }

    [Fact]
    public async Task ImportAll_OverwritesExistingYears()
    {
        // First import
        using var stream1 = MakeSieStream(SampleSie4WithBalances);
        var doc1 = _service.Parse(stream1);
        await _service.ImportAllAsync(doc1, overwrite: false);

        Assert.Equal(2, await _f.Db.FiscalYears.CountAsync());

        // Second import with overwrite
        using var stream2 = MakeSieStream(SampleSie4WithBalances);
        var doc2 = _service.Parse(stream2);
        var result = await _service.ImportAllAsync(doc2, overwrite: true);

        // Still 2 FYs, not duplicated
        Assert.Equal(2, await _f.Db.FiscalYears.CountAsync());
        Assert.Equal(2, result.FiscalYears.Count);
    }
}
