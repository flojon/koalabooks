using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class DeleteDraftAsyncTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public DeleteDraftAsyncTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task DeleteDraft_EntryNotFound_ReturnsError()
    {
        var error = await _f.JournalEntryService.DeleteDraftAsync(999);

        Assert.NotNull(error);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteDraft_PostedEntry_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 1000m);

        var error = await _f.JournalEntryService.DeleteDraftAsync(posted.Id);

        Assert.NotNull(error);
        Assert.Contains("posted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteDraft_ClosedFiscalYear_ReturnsError()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 500m);
        var (created, createError) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createError);
        Assert.NotNull(created);

        _fy.IsClosed = true;
        await _f.Db.SaveChangesAsync();

        var error = await _f.JournalEntryService.DeleteDraftAsync(created.Id);

        Assert.NotNull(error);
        Assert.Contains("closed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteDraft_ValidDraft_ReturnsNullAndRemovesEntry()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 750m);
        var (created, createError) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createError);
        Assert.NotNull(created);

        var error = await _f.JournalEntryService.DeleteDraftAsync(created.Id);

        Assert.Null(error);

        var remaining = await _f.Db.JournalEntries.FindAsync(created.Id);
        Assert.Null(remaining);
    }

    [Fact]
    public async Task DeleteDraft_ValidDraft_AlsoRemovesLines()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m);
        var (created, createError) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(createError);
        Assert.NotNull(created);

        var lineCountBefore = await _f.Db.JournalEntryLines
            .CountAsync(l => l.JournalEntryId == created.Id);
        Assert.True(lineCountBefore >= 2);

        await _f.JournalEntryService.DeleteDraftAsync(created.Id);

        var lineCountAfter = await _f.Db.JournalEntryLines
            .CountAsync(l => l.JournalEntryId == created.Id);
        Assert.Equal(0, lineCountAfter);
    }

    [Fact]
    public async Task DeleteDraft_PostedEntry_DoesNotRemoveEntry()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 300m);

        await _f.JournalEntryService.DeleteDraftAsync(posted.Id);

        var stillExists = await _f.Db.JournalEntries.FindAsync(posted.Id);
        Assert.NotNull(stillExists);
    }
}
