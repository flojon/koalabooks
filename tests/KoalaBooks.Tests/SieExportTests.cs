using System.Text;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

public class SieExportServiceTests : IDisposable
{
    private readonly TestFixture _f;

    public SieExportServiceTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    private async Task<FiscalYear> CreateFiscalYearAsync(
        string name = "2026",
        DateOnly? start = null,
        DateOnly? end = null)
    {
        var fy = new FiscalYear
        {
            Name = name,
            StartDate = start ?? new DateOnly(2026, 1, 1),
            EndDate = end ?? new DateOnly(2026, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.Add(fy);
        await _f.Db.SaveChangesAsync();
        return fy;
    }

    private async Task<Account> CreateAccountAsync(
        int fiscalYearId,
        string number,
        string name,
        AccountClass accountClass = AccountClass.Asset,
        decimal incomingBalance = 0,
        decimal outgoingBalance = 0)
    {
        var account = new Account
        {
            AccountNumber = number,
            Name = name,
            AccountClass = accountClass,
            FiscalYearId = fiscalYearId,
            IncomingBalance = incomingBalance,
            OutgoingBalance = outgoingBalance,
        };
        _f.Db.Accounts.Add(account);
        await _f.Db.SaveChangesAsync();
        return account;
    }

    private async Task<JournalEntry> CreateJournalEntryAsync(
        int fiscalYearId,
        int entryNumber,
        DateOnly date,
        string description,
        params (int accountId, decimal debit, decimal credit)[] lines)
    {
        var entry = new JournalEntry
        {
            EntryNumber = entryNumber,
            Date = date,
            Description = description,
            FiscalYearId = fiscalYearId,
            IsPosted = true,
        };
        _f.Db.JournalEntries.Add(entry);
        await _f.Db.SaveChangesAsync();

        foreach (var (accountId, debit, credit) in lines)
        {
            _f.Db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = entry.Id,
                AccountId = accountId,
                DebitAmount = debit,
                CreditAmount = credit,
            });
        }
        await _f.Db.SaveChangesAsync();
        return entry;
    }

    private static string DecodeExport(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437).GetString(bytes);
    }

    [Fact]
    public async Task Export_ContainsHeaderTags()
    {
        var fy = await CreateFiscalYearAsync();
        await CreateAccountAsync(fy.Id, "1910", "Kassa");

        var bytes = await _f.SieExportService.ExportAsync(fy.Id);
        var text = DecodeExport(bytes);

        Assert.Contains("#FLAGGA 0", text);
        Assert.Contains("#FORMAT PC8", text);
        Assert.Contains("#SIETYP 4", text);
        Assert.Contains("#PROGRAM \"KoalaBooks\" 1.0", text);
        Assert.Contains("#GEN", text);
        Assert.Contains("#FNAMN \"2026\"", text);
        Assert.Contains("#RAR 0 20260101 20261231", text);
    }

    [Fact]
    public async Task Export_UsesCompanyNameWhenProvided()
    {
        var fy = await CreateFiscalYearAsync();
        await CreateAccountAsync(fy.Id, "1910", "Kassa");

        var bytes = await _f.SieExportService.ExportAsync(fy.Id, "Koala AB");
        var text = DecodeExport(bytes);

        Assert.Contains("#FNAMN \"Koala AB\"", text);
    }

    [Fact]
    public async Task Export_ContainsAccounts()
    {
        var fy = await CreateFiscalYearAsync();
        await CreateAccountAsync(fy.Id, "1910", "Kassa");
        await CreateAccountAsync(fy.Id, "3010", "Försäljning", AccountClass.Revenue);
        await CreateAccountAsync(fy.Id, "5010", "Lokalhyra", AccountClass.Expense);

        var bytes = await _f.SieExportService.ExportAsync(fy.Id);
        var text = DecodeExport(bytes);

        Assert.Contains("#KONTO 1910 \"Kassa\"", text);
        Assert.Contains("#KONTO 3010 \"Försäljning\"", text);
        Assert.Contains("#KONTO 5010 \"Lokalhyra\"", text);
    }

    [Fact]
    public async Task Export_ContainsBalances()
    {
        var fy = await CreateFiscalYearAsync();
        await CreateAccountAsync(fy.Id, "1910", "Kassa", incomingBalance: 50000m);
        await CreateAccountAsync(fy.Id, "2440", "Leverantörsskuld", AccountClass.Liability, outgoingBalance: 15000m);
        await CreateAccountAsync(fy.Id, "3010", "Försäljning", AccountClass.Revenue);

        var bytes = await _f.SieExportService.ExportAsync(fy.Id);
        var text = DecodeExport(bytes);

        Assert.Contains("#IB 0 1910 50000.00", text);
        Assert.Contains("#UB 0 2440 -15000.00", text);
        Assert.DoesNotContain("#IB 0 3010", text);
        Assert.DoesNotContain("#UB 0 3010", text);
    }

    [Fact]
    public async Task Export_ContainsVouchers()
    {
        var fy = await CreateFiscalYearAsync();
        var kassa = await CreateAccountAsync(fy.Id, "1910", "Kassa");
        var hyra = await CreateAccountAsync(fy.Id, "5010", "Lokalhyra", AccountClass.Expense);

        await CreateJournalEntryAsync(fy.Id, 1, new DateOnly(2026, 1, 15), "Hyra januari",
            (hyra.Id, 10000m, 0m),
            (kassa.Id, 0m, 10000m));

        var bytes = await _f.SieExportService.ExportAsync(fy.Id);
        var text = DecodeExport(bytes);

        Assert.Contains("#VER \"\" 1 20260115 \"Hyra januari\"", text);
        Assert.Contains("#TRANS 5010 {} 10000.00 20260115 \"\"", text);
        Assert.Contains("#TRANS 1910 {} -10000.00 20260115 \"\"", text);
    }

    [Fact]
    public async Task Export_AmountSignConvention()
    {
        var fy = await CreateFiscalYearAsync();
        var bank = await CreateAccountAsync(fy.Id, "1930", "Bank");
        var revenue = await CreateAccountAsync(fy.Id, "3010", "Försäljning", AccountClass.Revenue);

        await CreateJournalEntryAsync(fy.Id, 1, new DateOnly(2026, 3, 1), "Inbetalning",
            (bank.Id, 5000m, 0m),
            (revenue.Id, 0m, 5000m));

        var bytes = await _f.SieExportService.ExportAsync(fy.Id);
        var text = DecodeExport(bytes);

        // Debit should be positive
        Assert.Contains("#TRANS 1930 {} 5000.00", text);
        // Credit should be negative
        Assert.Contains("#TRANS 3010 {} -5000.00", text);
    }

    [Fact]
    public async Task Export_RoundTrip()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var fy = await CreateFiscalYearAsync();
        var kassa = await CreateAccountAsync(fy.Id, "1910", "Kassa", incomingBalance: 25000m);
        var hyra = await CreateAccountAsync(fy.Id, "5010", "Lokalhyra", AccountClass.Expense);
        var bank = await CreateAccountAsync(fy.Id, "1930", "Bank", incomingBalance: 100000m);

        await CreateJournalEntryAsync(fy.Id, 1, new DateOnly(2026, 1, 15), "Hyra januari",
            (hyra.Id, 10000m, 0m),
            (bank.Id, 0m, 10000m));

        await CreateJournalEntryAsync(fy.Id, 2, new DateOnly(2026, 2, 15), "Hyra februari",
            (hyra.Id, 10000m, 0m),
            (bank.Id, 0m, 10000m));

        var exportedBytes = await _f.SieExportService.ExportAsync(fy.Id, "Koala AB");

        // Parse back using SieImportService
        var importService = new SieImportService(_f.Db, _f.Tenant);
        var stream = new MemoryStream(exportedBytes);
        var doc = importService.Parse(stream);

        // Verify header data
        Assert.Equal("Koala AB", doc.FNAMN?.Name);
        Assert.Equal(4, doc.SIETYP);

        // Verify accounts
        Assert.True(doc.KONTO.Count >= 3);
        Assert.Contains(doc.KONTO, k => k.Value.Number == "1910");
        Assert.Contains(doc.KONTO, k => k.Value.Number == "5010");
        Assert.Contains(doc.KONTO, k => k.Value.Number == "1930");

        // Verify vouchers
        Assert.Equal(2, doc.VER.Count);

        // Verify balances
        var ib1910 = doc.IB.FirstOrDefault(b => b.Account?.Number == "1910" && b.YearNr == 0);
        Assert.NotNull(ib1910);
        Assert.Equal(25000m, ib1910.Amount);

        var ib1930 = doc.IB.FirstOrDefault(b => b.Account?.Number == "1930" && b.YearNr == 0);
        Assert.NotNull(ib1930);
        Assert.Equal(100000m, ib1930.Amount);
    }
}
