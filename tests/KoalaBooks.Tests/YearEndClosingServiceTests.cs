using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// Year-end closing (bokslut) service tests.
/// Tests the three-phase workflow: Validate → Preview → Execute.
/// Spec: .squad/decisions/inbox/danny-yearend-closing-design.md
///
/// NOTE: If YearEndClosingService doesn't exist yet, these tests won't compile.
/// Linus is building the service in parallel. The tests define the expected contract.
/// </summary>
public class YearEndClosingServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly YearEndClosingService _service;
    private readonly JournalEntryService _journalService;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _cashAccount;
    private readonly Account _liabilityAccount;
    private readonly Account _equityAccount;
    private readonly Account _revenueAccount;
    private readonly Account _expenseAccount;

    public YearEndClosingServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        var fiscalYearService = new FiscalYearService(_db);
        _service = new YearEndClosingService(_db, fiscalYearService);
        _journalService = new JournalEntryService(_db);

        _fiscalYear = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(_fiscalYear);
        _db.SaveChanges();

        _cashAccount = new Account
        {
            AccountNumber = "1910", Name = "Kassa",
            AccountClass = AccountClass.Asset, FiscalYearId = _fiscalYear.Id
        };
        _liabilityAccount = new Account
        {
            AccountNumber = "2440", Name = "Leverantörsskulder",
            AccountClass = AccountClass.Liability, FiscalYearId = _fiscalYear.Id
        };
        _equityAccount = new Account
        {
            AccountNumber = "2081", Name = "Aktiekapital",
            AccountClass = AccountClass.Equity, FiscalYearId = _fiscalYear.Id
        };
        _revenueAccount = new Account
        {
            AccountNumber = "3010", Name = "Försäljning",
            AccountClass = AccountClass.Revenue, FiscalYearId = _fiscalYear.Id
        };
        _expenseAccount = new Account
        {
            AccountNumber = "5010", Name = "Lokalhyra",
            AccountClass = AccountClass.Expense, FiscalYearId = _fiscalYear.Id
        };

        _db.Accounts.AddRange(_cashAccount, _liabilityAccount, _equityAccount, _revenueAccount, _expenseAccount);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    #region Helpers

    private (Account a8999, Account a2099) AddResultAccounts()
    {
        var a8999 = new Account
        {
            AccountNumber = "8999", Name = "Årets resultat",
            AccountClass = AccountClass.Expense, FiscalYearId = _fiscalYear.Id
        };
        var a2099 = new Account
        {
            AccountNumber = "2099", Name = "Årets resultat",
            AccountClass = AccountClass.Equity, FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.AddRange(a8999, a2099);
        _db.SaveChanges();
        return (a8999, a2099);
    }

    private async Task CreateAndPostEntry(int debitAccountId, int creditAccountId, decimal amount)
    {
        var entry = new JournalEntry
        {
            Date = new DateOnly(2026, 6, 15),
            Description = $"Test entry {amount}",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
                new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
            ]
        };
        var (created, error) = await _journalService.CreateAsync(entry);
        Assert.Null(error);
        Assert.NotNull(created);
        var postError = await _journalService.PostAsync(created.Id);
        Assert.Null(postError);
    }

    /// <summary>
    /// Sets up a typical year: revenue 10,000 and expenses 6,000 → profit 4,000.
    /// </summary>
    private async Task SetupNormalYear()
    {
        await CreateAndPostEntry(_cashAccount.Id, _revenueAccount.Id, 10_000m);
        await CreateAndPostEntry(_expenseAccount.Id, _cashAccount.Id, 6_000m);
    }

    #endregion

    // ──────────────────────────────────────────────
    //  Validation tests
    // ──────────────────────────────────────────────

    #region Validation Tests

    [Fact]
    public async Task ValidateForClosing_WithUnpostedDrafts_ReturnsErrors()
    {
        await CreateAndPostEntry(_cashAccount.Id, _revenueAccount.Id, 1000m);

        // Leave a draft unposted
        var draft = new JournalEntry
        {
            Date = new DateOnly(2026, 6, 15),
            Description = "Draft entry",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _cashAccount.Id, DebitAmount = 500m, CreditAmount = 0 },
                new() { AccountId = _revenueAccount.Id, DebitAmount = 0, CreditAmount = 500m }
            ]
        };
        var (created, _) = await _journalService.CreateAsync(draft);
        Assert.NotNull(created);

        var result = await _service.ValidateForClosingAsync(_fiscalYear.Id);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ValidateForClosing_AlreadyClosed_ReturnsError()
    {
        _fiscalYear.IsClosed = true;
        await _db.SaveChangesAsync();

        var result = await _service.ValidateForClosingAsync(_fiscalYear.Id);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ValidateForClosing_ValidYear_ReturnsSuccess()
    {
        await CreateAndPostEntry(_cashAccount.Id, _revenueAccount.Id, 5000m);

        var result = await _service.ValidateForClosingAsync(_fiscalYear.Id);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateForClosing_YearNotFound_ReturnsError()
    {
        var result = await _service.ValidateForClosingAsync(99999);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    // ──────────────────────────────────────────────
    //  Preview tests
    // ──────────────────────────────────────────────

    #region Preview Tests

    [Fact]
    public async Task PreviewClosing_NormalYear_ShowsCorrectEntries()
    {
        AddResultAccounts();
        await SetupNormalYear(); // Revenue 10,000 — Expense 6,000

        var preview = await _service.PreviewClosingAsync(_fiscalYear.Id);

        Assert.True(preview.IsValid);
        Assert.Equal(4000m, preview.NetResult);
        Assert.Equal(2, preview.Entries.Count);
        Assert.Contains(preview.Entries, e => e.Description.Contains("Resultatdisposition"));
        Assert.Contains(preview.Entries, e =>
            e.Description.Contains("eget kapital", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewClosing_ZeroNetResult_SkipsEntry2()
    {
        AddResultAccounts();
        // Revenue = Expense = 5,000
        await CreateAndPostEntry(_cashAccount.Id, _revenueAccount.Id, 5000m);
        await CreateAndPostEntry(_expenseAccount.Id, _cashAccount.Id, 5000m);

        var preview = await _service.PreviewClosingAsync(_fiscalYear.Id);

        Assert.True(preview.IsValid);
        Assert.Equal(0m, preview.NetResult);
        Assert.Single(preview.Entries); // Only entry 1 — entry 2 skipped
    }

    [Fact]
    public async Task PreviewClosing_WithIBOnlyAccounts_IncludesInClosing()
    {
        AddResultAccounts();
        // Revenue account has IB from SIE import but no transactions this year
        _revenueAccount.IncomingBalance = 5000m;
        await _db.SaveChangesAsync();

        var preview = await _service.PreviewClosingAsync(_fiscalYear.Id);

        Assert.True(preview.IsValid);
        Assert.Equal(5000m, preview.NetResult);

        var entry1 = preview.Entries.First(e => e.Description.Contains("Resultatdisposition"));
        Assert.Contains(entry1.Lines, l => l.AccountNumber == _revenueAccount.AccountNumber);
    }

    [Fact]
    public async Task PreviewClosing_InvalidYear_ReturnsErrors()
    {
        _fiscalYear.IsClosed = true;
        await _db.SaveChangesAsync();

        var preview = await _service.PreviewClosingAsync(_fiscalYear.Id);

        Assert.False(preview.IsValid);
        Assert.NotEmpty(preview.Errors);
    }

    #endregion

    // ──────────────────────────────────────────────
    //  Execute tests
    // ──────────────────────────────────────────────

    #region Execute Tests

    [Fact]
    public async Task ExecuteClosing_NormalYear_CreatesClosingEntries()
    {
        AddResultAccounts();
        await SetupNormalYear();

        var result = await _service.ExecuteClosingAsync(_fiscalYear.Id);

        Assert.True(result.Success);

        var closingEntries = await _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.FiscalYearId == _fiscalYear.Id && j.IsClosingEntry)
            .OrderBy(j => j.EntryNumber)
            .ToListAsync();

        Assert.Equal(2, closingEntries.Count);

        // Entry 1: P&L → 8999 (Resultatdisposition)
        var entry1 = closingEntries[0];
        Assert.Contains("Resultatdisposition", entry1.Description);
        Assert.Equal(entry1.Lines.Sum(l => l.DebitAmount), entry1.Lines.Sum(l => l.CreditAmount));

        // Entry 2: 8999 → 2099 (transfer net result to equity)
        var entry2 = closingEntries[1];
        Assert.Contains("eget kapital", entry2.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(entry2.Lines.Sum(l => l.DebitAmount), entry2.Lines.Sum(l => l.CreditAmount));
    }

    [Fact]
    public async Task ExecuteClosing_SetsOutgoingBalancesCorrectly()
    {
        AddResultAccounts();
        await SetupNormalYear(); // Revenue 10,000 — Expense 6,000 → profit 4,000

        await _service.ExecuteClosingAsync(_fiscalYear.Id);

        var accounts = await _db.Accounts
            .Where(a => a.FiscalYearId == _fiscalYear.Id)
            .ToListAsync();

        // Asset (debit-normal): UB = IB + debits − credits = 0 + 10000 − 6000
        var cash = accounts.Single(a => a.AccountNumber == "1910");
        Assert.Equal(4000m, cash.OutgoingBalance);

        // Liability (credit-normal): no transactions → UB = 0
        var liability = accounts.Single(a => a.AccountNumber == "2440");
        Assert.Equal(0m, liability.OutgoingBalance);

        // Equity (2081): no transactions → UB = 0
        var equity = accounts.Single(a => a.AccountNumber == "2081");
        Assert.Equal(0m, equity.OutgoingBalance);

        // P&L accounts: UB = 0 (zeroed by closing entries)
        var revenue = accounts.Single(a => a.AccountNumber == "3010");
        Assert.Equal(0m, revenue.OutgoingBalance);
        var expense = accounts.Single(a => a.AccountNumber == "5010");
        Assert.Equal(0m, expense.OutgoingBalance);
        var a8999 = accounts.Single(a => a.AccountNumber == "8999");
        Assert.Equal(0m, a8999.OutgoingBalance);

        // 2099 (equity): holds net result = 4,000
        var a2099 = accounts.Single(a => a.AccountNumber == "2099");
        Assert.Equal(4000m, a2099.OutgoingBalance);
    }

    [Fact]
    public async Task ExecuteClosing_AutoCreates8999And2099_WhenMissing()
    {
        // Deliberately do NOT add result accounts
        await SetupNormalYear();

        var result = await _service.ExecuteClosingAsync(_fiscalYear.Id);

        Assert.True(result.Success);

        var accounts = await _db.Accounts
            .Where(a => a.FiscalYearId == _fiscalYear.Id)
            .ToListAsync();

        var a8999 = accounts.SingleOrDefault(a => a.AccountNumber == "8999");
        Assert.NotNull(a8999);
        Assert.Equal(AccountClass.Expense, a8999.AccountClass);

        var a2099 = accounts.SingleOrDefault(a => a.AccountNumber == "2099");
        Assert.NotNull(a2099);
        Assert.Equal(AccountClass.Equity, a2099.AccountClass);
    }

    [Fact]
    public async Task ExecuteClosing_ClosingEntriesArePostedAndMarked()
    {
        AddResultAccounts();
        await SetupNormalYear();

        await _service.ExecuteClosingAsync(_fiscalYear.Id);

        var closingEntries = await _db.JournalEntries
            .Where(j => j.FiscalYearId == _fiscalYear.Id && j.IsClosingEntry)
            .ToListAsync();

        Assert.NotEmpty(closingEntries);
        Assert.All(closingEntries, e =>
        {
            Assert.True(e.IsPosted);
            Assert.True(e.IsClosingEntry);
        });
    }

    [Fact]
    public async Task ExecuteClosing_ZeroResult_SkipsEntry2()
    {
        AddResultAccounts();
        // Revenue = Expense = 5,000
        await CreateAndPostEntry(_cashAccount.Id, _revenueAccount.Id, 5000m);
        await CreateAndPostEntry(_expenseAccount.Id, _cashAccount.Id, 5000m);

        await _service.ExecuteClosingAsync(_fiscalYear.Id);

        var closingEntries = await _db.JournalEntries
            .Where(j => j.FiscalYearId == _fiscalYear.Id && j.IsClosingEntry)
            .ToListAsync();

        Assert.Single(closingEntries);
        Assert.Contains("Resultatdisposition", closingEntries[0].Description);
    }

    [Fact]
    public async Task ExecuteClosing_SetsIsClosed_And_ClosedAt()
    {
        AddResultAccounts();
        await SetupNormalYear();

        var beforeExecute = DateTime.UtcNow;
        await _service.ExecuteClosingAsync(_fiscalYear.Id);

        var fy = await _db.FiscalYears.FindAsync(_fiscalYear.Id);
        Assert.NotNull(fy);
        Assert.True(fy.IsClosed);
        Assert.NotNull(fy.ClosedAt);
        Assert.True(fy.ClosedAt >= beforeExecute);
    }

    [Fact]
    public async Task ExecuteClosing_PropagatesBalancesToNextYear()
    {
        AddResultAccounts();
        await SetupNormalYear(); // profit 4,000 → cash UB = 4,000

        // Create next fiscal year with a cash account
        var nextYear = new FiscalYear
        {
            Name = "2027",
            StartDate = new DateOnly(2027, 1, 1),
            EndDate = new DateOnly(2027, 12, 31)
        };
        _db.FiscalYears.Add(nextYear);
        await _db.SaveChangesAsync();

        _db.Accounts.Add(new Account
        {
            AccountNumber = "1910", Name = "Kassa",
            AccountClass = AccountClass.Asset, FiscalYearId = nextYear.Id,
            IncomingBalance = 0m
        });
        await _db.SaveChangesAsync();

        await _service.ExecuteClosingAsync(_fiscalYear.Id);

        // Next year's cash IB should be updated to closed year's cash UB
        var nextCash = await _db.Accounts.SingleAsync(
            a => a.FiscalYearId == nextYear.Id && a.AccountNumber == "1910");
        Assert.Equal(4000m, nextCash.IncomingBalance);
    }

    [Fact]
    public async Task ExecuteClosing_NoTransactions_StillCloses()
    {
        AddResultAccounts();
        // Dormant year — no journal entries at all

        var result = await _service.ExecuteClosingAsync(_fiscalYear.Id);

        Assert.True(result.Success);

        var fy = await _db.FiscalYears.FindAsync(_fiscalYear.Id);
        Assert.True(fy!.IsClosed);

        // No closing entries needed (no P&L balances to close)
        var closingEntries = await _db.JournalEntries
            .Where(j => j.FiscalYearId == _fiscalYear.Id && j.IsClosingEntry)
            .ToListAsync();
        Assert.Empty(closingEntries);

        // B/S accounts: UB = IB (all zero)
        var cash = await _db.Accounts.SingleAsync(
            a => a.FiscalYearId == _fiscalYear.Id && a.AccountNumber == "1910");
        Assert.Equal(0m, cash.OutgoingBalance);
    }

    [Fact]
    public async Task ExecuteClosing_BlockedByUnpostedDrafts()
    {
        AddResultAccounts();

        // Create a draft (not posted)
        var draft = new JournalEntry
        {
            Date = new DateOnly(2026, 6, 15),
            Description = "Draft entry",
            FiscalYearId = _fiscalYear.Id,
            Lines =
            [
                new() { AccountId = _cashAccount.Id, DebitAmount = 1000m, CreditAmount = 0 },
                new() { AccountId = _revenueAccount.Id, DebitAmount = 0, CreditAmount = 1000m }
            ]
        };
        await _journalService.CreateAsync(draft);

        var result = await _service.ExecuteClosingAsync(_fiscalYear.Id);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);

        // Year must NOT have been closed
        var fy = await _db.FiscalYears.FindAsync(_fiscalYear.Id);
        Assert.False(fy!.IsClosed);
    }

    #endregion

    // ──────────────────────────────────────────────
    //  Edge case tests
    // ──────────────────────────────────────────────

    #region Edge Case Tests

    [Fact]
    public async Task ExecuteClosing_EntryNumbersAreSequential()
    {
        AddResultAccounts();
        await SetupNormalYear(); // 2 regular entries → numbers 1, 2

        await _service.ExecuteClosingAsync(_fiscalYear.Id);

        var allEntries = await _db.JournalEntries
            .Where(j => j.FiscalYearId == _fiscalYear.Id)
            .OrderBy(j => j.EntryNumber)
            .ToListAsync();

        // All entry numbers must be sequential (1, 2, 3, 4)
        for (int i = 0; i < allEntries.Count; i++)
        {
            Assert.Equal(i + 1, allEntries[i].EntryNumber);
        }
    }

    [Fact]
    public async Task ExecuteClosing_ClosingEntryDatesMatchFiscalYearEnd()
    {
        AddResultAccounts();
        await SetupNormalYear();

        await _service.ExecuteClosingAsync(_fiscalYear.Id);

        var closingEntries = await _db.JournalEntries
            .Where(j => j.FiscalYearId == _fiscalYear.Id && j.IsClosingEntry)
            .ToListAsync();

        Assert.All(closingEntries, e =>
        {
            Assert.Equal(_fiscalYear.EndDate, e.Date);
        });
    }

    #endregion
}
