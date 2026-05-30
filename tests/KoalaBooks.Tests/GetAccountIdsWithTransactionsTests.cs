using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class GetAccountIdsWithTransactionsTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;
    private readonly Account _expense;

    public GetAccountIdsWithTransactionsTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        _cash = _f.CreateAccount(_fy.Id, "1910", "Kassa", AccountClass.Asset);
        _revenue = _f.CreateAccount(_fy.Id, "3010", "Försäljning", AccountClass.Revenue);
        _expense = _f.CreateAccount(_fy.Id, "5010", "Lokalhyra", AccountClass.Expense);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task ReturnsAccountIds_ForPostedTransactions()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 1000m);

        var ids = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(_fy.Id);

        Assert.Contains(_cash.Id, ids);
        Assert.Contains(_revenue.Id, ids);
        Assert.DoesNotContain(_expense.Id, ids);
    }

    [Fact]
    public async Task ExcludesClosingEntries_ByDefault()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 500m);
        await CreateClosingEntryAsync(_revenue.Id, _expense.Id, 500m);

        var ids = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(_fy.Id);

        Assert.Contains(_cash.Id, ids);
        Assert.Contains(_revenue.Id, ids);
        Assert.DoesNotContain(_expense.Id, ids);
    }

    [Fact]
    public async Task IncludesClosingEntries_WhenRequested()
    {
        await CreateClosingEntryAsync(_revenue.Id, _expense.Id, 500m);

        var ids = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(
            _fy.Id, includeClosingEntries: true);

        Assert.Contains(_revenue.Id, ids);
        Assert.Contains(_expense.Id, ids);
    }

    [Fact]
    public async Task AccountWithOnlyClosingEntries_IsExcluded_ByDefault_ButIncluded_WhenRequested()
    {
        // _expense only touched by a closing entry
        await CreateClosingEntryAsync(_revenue.Id, _expense.Id, 500m);

        var withoutClosing = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(_fy.Id);
        var withClosing = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(
            _fy.Id, includeClosingEntries: true);

        Assert.DoesNotContain(_expense.Id, withoutClosing);
        Assert.Contains(_expense.Id, withClosing);
    }

    [Fact]
    public async Task ExcludesDraftEntries()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m);
        var (created, _) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.NotNull(created);
        // intentionally NOT posted

        var ids = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(_fy.Id);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task DateRangeFilter_LimitsResults()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 100m,
            date: new DateOnly(2026, 1, 10));
        await _f.CreateAndPostEntryAsync(_fy.Id, _expense.Id, _cash.Id, 200m,
            date: new DateOnly(2026, 6, 1));

        var ids = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(
            _fy.Id, from: new DateOnly(2026, 5, 1), to: new DateOnly(2026, 12, 31));

        Assert.Contains(_expense.Id, ids);
        Assert.Contains(_cash.Id, ids);
        // January revenue entry is outside the range; revenue account not touched in range
        Assert.DoesNotContain(_revenue.Id, ids);
    }

    [Fact]
    public async Task DateRangeFilter_WorksWithIncludeClosingEntries()
    {
        await CreateClosingEntryAsync(_revenue.Id, _expense.Id, 500m,
            date: new DateOnly(2026, 12, 31));

        var ids = await _f.JournalEntryService.GetAccountIdsWithTransactionsAsync(
            _fy.Id,
            from: new DateOnly(2026, 12, 1),
            to: new DateOnly(2026, 12, 31),
            includeClosingEntries: true);

        Assert.Contains(_revenue.Id, ids);
        Assert.Contains(_expense.Id, ids);
    }

    private async Task CreateClosingEntryAsync(
        int debitAccountId, int creditAccountId, decimal amount,
        DateOnly? date = null)
    {
        var entry = new JournalEntry
        {
            Date = date ?? new DateOnly(2026, 12, 31),
            Description = "Closing entry",
            FiscalYearId = _fy.Id,
            IsPosted = true,
            IsClosingEntry = true,
            Lines =
            [
                new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
                new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
            ]
        };
        _f.Db.JournalEntries.Add(entry);
        await _f.Db.SaveChangesAsync();
    }
}
