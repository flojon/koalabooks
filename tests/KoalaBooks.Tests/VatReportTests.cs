using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class VatReportTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _outputVat25;  // 2610 Utgående moms 25%
    private readonly Account _outputVat12;  // 2611 Utgående moms 12%
    private readonly Account _inputVat;     // 2640 Ingående moms

    public VatReportTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        _cash = _f.CreateAccount(_fy.Id, "1910", "Kassa", AccountClass.Asset);
        _outputVat25 = _f.CreateAccount(_fy.Id, "2610", "Utgående moms 25%", AccountClass.Liability);
        _outputVat12 = _f.CreateAccount(_fy.Id, "2611", "Utgående moms 12%", AccountClass.Liability);
        _inputVat = _f.CreateAccount(_fy.Id, "2640", "Ingående moms", AccountClass.Asset);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task VatReport_WithOutputAndInputVat_ComputesCorrectNetPayable()
    {
        // Output VAT: 2500 credit to 2610 (25% on 10,000 sale)
        await PostEntry(_cash.Id, _outputVat25.Id, 2500m);
        // Input VAT: 500 debit from 2640 (25% on 2,000 purchase)
        await PostEntry(_inputVat.Id, _cash.Id, 500m);

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        Assert.Equal(2500m, data.OutputVat.Total);
        Assert.Equal(500m, data.InputVat.Total);
        Assert.Equal(2000m, data.NetPayable);  // 2500 - 500 = pay to Skatteverket
    }

    [Fact]
    public async Task VatReport_MoreInputThanOutput_NegativeNetPayable()
    {
        // More input VAT than output → refund scenario
        await PostEntry(_cash.Id, _outputVat25.Id, 500m);
        await PostEntry(_inputVat.Id, _cash.Id, 2000m);

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        Assert.Equal(500m, data.OutputVat.Total);
        Assert.Equal(2000m, data.InputVat.Total);
        Assert.Equal(-1500m, data.NetPayable);  // negative = återbetalning
    }

    [Fact]
    public async Task VatReport_DateRangeFilter_ExcludesOutOfRangeTransactions()
    {
        await PostEntry(_cash.Id, _outputVat25.Id, 2500m, date: new DateOnly(2026, 1, 15));  // Q1
        await PostEntry(_cash.Id, _outputVat25.Id, 1000m, date: new DateOnly(2026, 4, 10));  // Q2

        var q1Data = await _f.JournalEntryService.GetVatReportAsync(
            _fy.Id,
            from: new DateOnly(2026, 1, 1),
            to: new DateOnly(2026, 3, 31));

        Assert.Equal(2500m, q1Data.OutputVat.Total);
        Assert.Single(q1Data.OutputVat.Rows);
    }

    [Fact]
    public async Task VatReport_MultipleOutputVatAccounts_SumsCorrectly()
    {
        await PostEntry(_cash.Id, _outputVat25.Id, 2500m);  // 25% VAT
        await PostEntry(_cash.Id, _outputVat12.Id, 600m);   // 12% VAT

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        Assert.Equal(2, data.OutputVat.Rows.Count);
        Assert.Equal(3100m, data.OutputVat.Total);
    }

    [Fact]
    public async Task VatReport_ClosingEntriesExcluded()
    {
        await PostEntry(_cash.Id, _outputVat25.Id, 2500m);

        // Simulate a closing entry touching a VAT account (should not happen in practice,
        // but the filter must hold regardless)
        var closing = new JournalEntry
        {
            Date = new DateOnly(2026, 12, 31),
            Description = "Closing",
            FiscalYearId = _fy.Id,
            IsPosted = true,
            IsClosingEntry = true,
            Lines =
            [
                new() { AccountId = _cash.Id, DebitAmount = 2500m, CreditAmount = 0 },
                new() { AccountId = _outputVat25.Id, DebitAmount = 0, CreditAmount = 2500m }
            ]
        };
        _f.Db.JournalEntries.Add(closing);
        await _f.Db.SaveChangesAsync();

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        // Only the normal entry counts — closing entry is excluded
        Assert.Equal(2500m, data.OutputVat.Total);
        Assert.Single(data.OutputVat.Rows);
        Assert.Equal(2500m, data.OutputVat.Rows[0].Credit);
    }

    [Fact]
    public async Task VatReport_EmptyPeriod_ReturnsBothSectionsEmpty()
    {
        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        Assert.Empty(data.OutputVat.Rows);
        Assert.Empty(data.InputVat.Rows);
        Assert.Equal(0m, data.NetPayable);
    }

    private async Task PostEntry(int debitAccountId, int creditAccountId, decimal amount, DateOnly? date = null)
        => await _f.CreateAndPostEntryAsync(_fy.Id, debitAccountId, creditAccountId, amount, date);
}
