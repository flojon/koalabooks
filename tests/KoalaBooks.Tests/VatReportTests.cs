using KoalaBooks.Application.Services;
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

    [Fact]
    public async Task VatReport_UnpostedDraft_IsExcluded()
    {
        // Posted entry → counts
        await PostEntry(_cash.Id, _outputVat25.Id, 1000m);

        // Draft (created but never posted) → must not count
        var draft = new JournalEntry
        {
            Date = new DateOnly(2026, 2, 1),
            Description = "Draft sale",
            FiscalYearId = _fy.Id,
            Lines =
            [
                new() { AccountId = _cash.Id, DebitAmount = 5000m, CreditAmount = 0 },
                new() { AccountId = _outputVat25.Id, DebitAmount = 0, CreditAmount = 5000m }
            ]
        };
        var (created, error) = await _f.JournalEntryService.CreateAsync(draft);
        Assert.Null(error);
        Assert.NotNull(created);

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        Assert.Equal(1000m, data.OutputVat.Total);
    }

    [Fact]
    public async Task VatReport_AccountsOutsideVatRange_AreIgnored()
    {
        // Settlement account 2650 sits outside 2610–2649 and must not appear
        var settlement = _f.CreateAccount(_fy.Id, "2650", "Redovisningskonto moms", AccountClass.Liability);
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, settlement.Id, 1500m);

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        Assert.DoesNotContain(data.OutputVat.Rows, r => r.AccountNumber == "2650");
        Assert.DoesNotContain(data.InputVat.Rows, r => r.AccountNumber == "2650");
    }

    [Fact]
    public async Task VatReport_OtherFiscalYear_IsNotIncluded()
    {
        await PostEntry(_cash.Id, _outputVat25.Id, 2500m);

        // Independent fiscal year with its own VAT account and posted entry
        var otherFy = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var otherCash = _f.CreateAccount(otherFy.Id, "1910", "Kassa", AccountClass.Asset);
        var otherVat = _f.CreateAccount(otherFy.Id, "2610", "Utgående moms 25%", AccountClass.Liability);
        await _f.CreateAndPostEntryAsync(otherFy.Id, otherCash.Id, otherVat.Id, 9999m,
            date: new DateOnly(2025, 6, 1));

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        Assert.Equal(2500m, data.OutputVat.Total);
    }

    [Fact]
    public async Task VatReport_AccountWithZeroNetActivity_IsOmittedFromRows()
    {
        // 2611 is created in the fixture but never touched → must not appear as a row
        await PostEntry(_cash.Id, _outputVat25.Id, 2500m);

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);

        var row = Assert.Single(data.OutputVat.Rows);
        Assert.Equal("2610", row.AccountNumber);
        Assert.DoesNotContain(data.OutputVat.Rows, r => r.AccountNumber == "2611");
    }

    [Fact]
    public async Task CsvExporter_PositiveNet_RendersHeadersTotalsAndPayableLabel()
    {
        await PostEntry(_cash.Id, _outputVat25.Id, 2500m);
        await PostEntry(_inputVat.Id, _cash.Id, 500m);

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);
        var bytes = new VatReportCsvExporter().Build(
            data, _fy.Name,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

        var csv = DecodeUtf8(bytes);

        Assert.Contains("Momsredovisning;2026", csv);
        Assert.Contains("Period;2026-01-01 — 2026-03-31", csv);
        Assert.Contains("Utgående moms", csv);
        Assert.Contains("Ingående moms", csv);
        Assert.Contains("Konto;Namn;Debet;Kredit;Netto", csv);
        Assert.Contains("2610;Utgående moms 25%;", csv);
        Assert.Contains("2640;Ingående moms;", csv);
        // sv-SE → comma decimal
        Assert.Contains("2500,00", csv);
        Assert.Contains("500,00", csv);
        Assert.Contains("Moms att betala;2000,00", csv);
    }

    [Fact]
    public async Task CsvExporter_NegativeNet_LabelsAsRefund()
    {
        await PostEntry(_cash.Id, _outputVat25.Id, 500m);
        await PostEntry(_inputVat.Id, _cash.Id, 2000m);

        var data = await _f.JournalEntryService.GetVatReportAsync(_fy.Id);
        var bytes = new VatReportCsvExporter().Build(data, _fy.Name, null, null);
        var csv = DecodeUtf8(bytes);

        Assert.Contains("Moms att återfå;1500,00", csv);
        Assert.DoesNotContain("Period;", csv);
    }

    [Fact]
    public void CsvExporter_AccountNameContainingSeparator_IsQuoted()
    {
        var data = new VatReportData
        {
            OutputVat = new VatReportSection
            {
                Title = "Utgående moms",
                Total = 100m,
                Rows =
                [
                    new VatReportRow
                    {
                        AccountNumber = "2610",
                        AccountName = "Namn med ; semikolon",
                        Debit = 0m,
                        Credit = 100m
                    }
                ]
            },
            InputVat = new VatReportSection { Title = "Ingående moms", Total = 0m, Rows = [] },
            NetPayable = 100m
        };

        var bytes = new VatReportCsvExporter().Build(data, "2026", null, null);
        var csv = DecodeUtf8(bytes);

        Assert.Contains("\"Namn med ; semikolon\"", csv);
    }

    [Fact]
    public void CsvExporter_StartsWithUtf8Bom()
    {
        var data = new VatReportData
        {
            OutputVat = new VatReportSection { Title = "Utgående moms", Total = 0m, Rows = [] },
            InputVat = new VatReportSection { Title = "Ingående moms", Total = 0m, Rows = [] },
            NetPayable = 0m
        };

        var bytes = new VatReportCsvExporter().Build(data, "2026", null, null);

        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    private async Task PostEntry(int debitAccountId, int creditAccountId, decimal amount, DateOnly? date = null)
        => await _f.CreateAndPostEntryAsync(_fy.Id, debitAccountId, creditAccountId, amount, date);

    private static string DecodeUtf8(byte[] bytes)
    {
        // Skip UTF-8 BOM if present
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
