using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class JournalEntryServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _account1;
    private readonly Account _account2;

    public JournalEntryServiceTests()
    {
        _f = new TestFixture();
        _fiscalYear = _f.CreateFiscalYear();
        _account1 = _f.CreateAccount(_fiscalYear.Id, "1910", "Kassa");
        _account2 = _f.CreateAccount(_fiscalYear.Id, "3010", "Försäljning", AccountClass.Revenue);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task CreateEntry_BalancedEntry_Succeeds()
    {
        var entry = MakeEntry(1000m);
        var (result, error) = await _f.JournalEntryService.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(1, result.EntryNumber);
    }

    [Fact]
    public async Task CreateEntry_UnbalancedEntry_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "Unbalanced",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 1000, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = 500 }
            ]
        };

        var (result, error) = await _f.JournalEntryService.CreateAsync(entry);

        Assert.NotNull(error);
        Assert.Contains("Debit", error);
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateEntry_LessThanTwoLines_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "One line",
            FiscalYearId = _fiscalYear.Id,
            Lines = [new() { AccountId = _account1.Id, DebitAmount = 100, CreditAmount = 0 }]
        };

        var (_, error) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Contains("at least 2 lines", error);
    }

    [Fact]
    public async Task CreateEntry_ClosedFiscalYear_Fails()
    {
        _fiscalYear.IsClosed = true;
        await _f.Db.SaveChangesAsync();

        var entry = MakeEntry(500m);
        var (_, error) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Contains("closed", error);
    }

    [Fact]
    public async Task CreateEntry_SequentialEntryNumbers()
    {
        var (e1, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(100));
        var (e2, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(200));

        Assert.Equal(1, e1!.EntryNumber);
        Assert.Equal(2, e2!.EntryNumber);
    }

    [Fact]
    public async Task CreateEntry_NegativeAmount_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "Negative",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = -100, CreditAmount = 0 },
                new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = -100 }
            ]
        };

        var (_, error) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Contains("negative", error);
    }

    [Fact]
    public async Task CreateEntry_BothDebitAndCredit_Fails()
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 3, 1),
            Description = "Both",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _account1.Id, DebitAmount = 100, CreditAmount = 50 },
                new() { AccountId = _account2.Id, DebitAmount = 50, CreditAmount = 100 }
            ]
        };

        var (_, error) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Contains("both debit and credit", error);
    }

    [Fact]
    public async Task GetTrialBalance_ReturnsCorrectTotals()
    {
        await _f.JournalEntryService.CreateAsync(MakeEntry(1000));
        await _f.JournalEntryService.CreateAsync(MakeEntry(500));

        var rows = await _f.JournalEntryService.GetTrialBalanceAsync(_fiscalYear.Id);

        Assert.Equal(2, rows.Count);
        var debitAccount = rows.Single(r => r.AccountNumber == "1910");
        Assert.Equal(1500, debitAccount.TotalDebit);
        Assert.Equal(0, debitAccount.TotalCredit);
    }

    [Fact]
    public async Task GetDraftsForOrganisationAsync_SpansMultipleOpenFiscalYears()
    {
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var acc1 = _f.CreateAccount(fy2025.Id, "1910", "Kassa");
        var acc2 = _f.CreateAccount(fy2025.Id, "2440", "Lev.skulder");
        var acc3 = _f.CreateAccount(fy2026.Id, "1910", "Kassa");
        var acc4 = _f.CreateAccount(fy2026.Id, "2440", "Lev.skulder");

        var draft2025 = _f.MakeEntry(fy2025.Id, acc1.Id, acc2.Id, 100, new DateOnly(2025, 6, 1));
        var draft2026 = _f.MakeEntry(fy2026.Id, acc3.Id, acc4.Id, 200, new DateOnly(2026, 6, 1));
        _f.Db.JournalEntries.AddRange(draft2025, draft2026);
        await _f.Db.SaveChangesAsync();

        var drafts = await _f.JournalEntryService.GetDraftsForOrganisationAsync();
        var count = await _f.JournalEntryService.CountDraftsForOrganisationAsync();

        Assert.Equal(2, drafts.Count);
        Assert.Equal(2, count);
        Assert.Contains(drafts, d => d.Id == draft2025.Id);
        Assert.Contains(drafts, d => d.Id == draft2026.Id);
    }

    [Fact]
    public async Task GetDraftsForOrganisationAsync_ExcludesPostedEntries()
    {
        var fy = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var acc1 = _f.CreateAccount(fy.Id, "1910", "Kassa");
        var acc2 = _f.CreateAccount(fy.Id, "2440", "Lev.skulder");
        var posted = _f.MakeEntry(fy.Id, acc1.Id, acc2.Id, 100, new DateOnly(2026, 6, 1));
        posted.IsPosted = true;
        _f.Db.JournalEntries.Add(posted);
        await _f.Db.SaveChangesAsync();

        var drafts = await _f.JournalEntryService.GetDraftsForOrganisationAsync();

        Assert.Empty(drafts);
    }

    private JournalEntry MakeEntry(decimal amount) => new()
    {
        Date = new DateOnly(2026, 3, 1),
        Description = $"Test entry {amount}",
        FiscalYearId = _fiscalYear.Id,
        IsPosted = true,
        Lines =
        [
            new() { AccountId = _account1.Id, DebitAmount = amount, CreditAmount = 0 },
            new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = amount }
        ]
    };
}
