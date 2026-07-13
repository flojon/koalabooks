using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class JournalEntryDbGuardTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public JournalEntryDbGuardTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task DirectRemove_PostedEntry_ThrowsOnSaveChanges()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 500m);

        _f.Db.JournalEntries.Remove(posted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task DirectRemove_ReversedEntry_ThrowsOnSaveChanges()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 500m);
        await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Oops");

        var reloaded = await _f.Db.JournalEntries.FindAsync(posted.Id);
        _f.Db.JournalEntries.Remove(reloaded!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task DirectRemove_DraftEntry_Succeeds()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m);
        var (created, error) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(error);

        _f.Db.JournalEntries.Remove(created!);
        await _f.Db.SaveChangesAsync();

        var remaining = await _f.Db.JournalEntries.FindAsync(created!.Id);
        Assert.Null(remaining);
    }

    [Fact]
    public async Task DirectRemove_YearEndClosingEntry_ThrowsOnSaveChanges()
    {
        // Closing entries are created by YearEndClosingService, not JournalEntryService.PostAsync —
        // this proves the DB guard protects posted vouchers regardless of which code path created them.
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 10_000m);

        var result = await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);
        Assert.True(result.Success);

        var closingEntry = await _f.Db.JournalEntries
            .FirstAsync(j => j.FiscalYearId == _fy.Id && j.IsClosingEntry);

        _f.Db.JournalEntries.Remove(closingEntry);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.Db.SaveChangesAsync());
    }
}
