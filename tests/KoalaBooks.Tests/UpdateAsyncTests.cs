using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class UpdateAsyncTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public UpdateAsyncTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task UpdateAsync_ZeroLines_ReturnsError()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 500m);
        var (created, createError) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createError);
        Assert.NotNull(created);

        var updateEntry = new JournalEntry
        {
            Id = created.Id,
            Date = new DateOnly(2026, 6, 15),
            Description = "Updated",
            FiscalYearId = _fy.Id,
            Lines = []
        };

        var (updated, error) = await _f.JournalEntryService.UpdateAsync(updateEntry);

        Assert.Null(updated);
        Assert.NotNull(error);
        Assert.Contains("at least 2 lines", error);
    }

    [Fact]
    public async Task UpdateAsync_DebitsNotEqualCredits_ReturnsError()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 500m);
        var (created, createError) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createError);
        Assert.NotNull(created);

        var updateEntry = new JournalEntry
        {
            Id = created.Id,
            Date = new DateOnly(2026, 6, 15),
            Description = "Updated",
            FiscalYearId = _fy.Id,
            Lines =
            [
                new() { AccountId = _cash.Id, DebitAmount = 1000m, CreditAmount = 0 },
                new() { AccountId = _revenue.Id, DebitAmount = 0, CreditAmount = 500m }
            ]
        };

        var (updated, error) = await _f.JournalEntryService.UpdateAsync(updateEntry);

        Assert.Null(updated);
        Assert.NotNull(error);
        Assert.Contains("Debit", error);
    }

    [Fact]
    public async Task UpdateAsync_ValidDraft_UpdatesSuccessfully()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 500m);
        var (created, createError) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createError);
        Assert.NotNull(created);

        var updateEntry = new JournalEntry
        {
            Id = created.Id,
            Date = new DateOnly(2026, 7, 1),
            Description = "Updated description",
            FiscalYearId = _fy.Id,
            Lines =
            [
                new() { AccountId = _cash.Id, DebitAmount = 750m, CreditAmount = 0 },
                new() { AccountId = _revenue.Id, DebitAmount = 0, CreditAmount = 750m }
            ]
        };

        var (updated, error) = await _f.JournalEntryService.UpdateAsync(updateEntry);

        Assert.NotNull(updated);
        Assert.Null(error);
        Assert.Equal("Updated description", updated.Description);
        Assert.Equal(new DateOnly(2026, 7, 1), updated.Date);
    }

    [Fact]
    public async Task UpdateAsync_PostedEntry_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 500m);

        var updateEntry = new JournalEntry
        {
            Id = posted.Id,
            Date = new DateOnly(2026, 7, 1),
            Description = "Try update posted",
            FiscalYearId = _fy.Id,
            Lines =
            [
                new() { AccountId = _cash.Id, DebitAmount = 750m, CreditAmount = 0 },
                new() { AccountId = _revenue.Id, DebitAmount = 0, CreditAmount = 750m }
            ]
        };

        var (updated, error) = await _f.JournalEntryService.UpdateAsync(updateEntry);

        Assert.Null(updated);
        Assert.NotNull(error);
        Assert.Contains("posted", error, StringComparison.OrdinalIgnoreCase);
    }
}
