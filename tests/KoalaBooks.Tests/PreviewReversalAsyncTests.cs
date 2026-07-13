using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class PreviewReversalAsyncTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public PreviewReversalAsyncTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task PreviewReversalAsync_MatchesWhatCreateReversalAsyncProduces()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);

        var (preview, previewError) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Wrong amount");
        Assert.Null(previewError);
        Assert.NotNull(preview);

        var (created, createError) = await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Wrong amount");
        Assert.Null(createError);
        Assert.NotNull(created);

        Assert.Equal(preview!.EntryNumber, created!.EntryNumber);
        Assert.Equal(preview.Date, created.Date);
        Assert.Equal(preview.Description, created.Description);
        Assert.Equal(
            preview.Lines.Select(l => (l.AccountId, l.DebitAmount, l.CreditAmount)).ToList(),
            created.Lines.Select(l => (l.AccountId, l.DebitAmount, l.CreditAmount)).ToList());
    }

    [Fact]
    public async Task PreviewReversalAsync_DoesNotPersistAnythingOrMutateOriginal()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 250m);

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Just checking");
        Assert.Null(error);
        Assert.NotNull(preview);

        var count = await _f.Db.JournalEntries.CountAsync();
        Assert.Equal(1, count); // only the original — preview created nothing

        var reloadedOriginal = await _f.Db.JournalEntries.FindAsync(posted.Id);
        Assert.Equal(JournalEntryStatus.Posted, reloadedOriginal!.Status); // not flipped to Reversed
    }

    [Fact]
    public async Task PreviewReversalAsync_EntryNotPosted_ReturnsError()
    {
        var draft = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m);
        var (created, _) = await _f.JournalEntryService.CreateAsync(draft);

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(created!.Id, "n/a");

        Assert.Null(preview);
        Assert.NotNull(error);
        Assert.Contains("Can only reverse posted entries", error);
    }

    [Fact]
    public async Task PreviewReversalAsync_AlreadyReversedEntry_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);
        await _f.JournalEntryService.CreateReversalAsync(posted.Id, "First reversal");

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Second attempt");

        Assert.Null(preview);
        Assert.NotNull(error);
        Assert.Contains("already been reversed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewReversalAsync_ClosedFiscalYear_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);

        _fy.IsClosed = true;
        await _f.Db.SaveChangesAsync();

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Correction");

        Assert.Null(preview);
        Assert.NotNull(error);
        Assert.Contains("closed", error, StringComparison.OrdinalIgnoreCase);
    }
}
