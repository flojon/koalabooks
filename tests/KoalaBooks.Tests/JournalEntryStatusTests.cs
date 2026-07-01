using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class JournalEntryStatusTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public JournalEntryStatusTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task NewDraftEntry_DefaultsToStatusDraft()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m);
        var (created, error) = await _f.JournalEntryService.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(created);
        Assert.Equal(JournalEntryStatus.Draft, created.Status);

        var reloaded = await _f.Db.JournalEntries.FindAsync(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(JournalEntryStatus.Draft, reloaded!.Status);
        Assert.Null(reloaded.SourceJournalEntryId);
    }

    [Fact]
    public async Task PostAsync_SetsStatusToPosted()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m);
        var (created, _) = await _f.JournalEntryService.CreateAsync(entry);

        await _f.JournalEntryService.PostAsync(created!.Id);

        var reloaded = await _f.Db.JournalEntries.FindAsync(created.Id);
        Assert.Equal(JournalEntryStatus.Posted, reloaded!.Status);
    }

    [Fact]
    public async Task CreateReversalAsync_MarksOriginalReversedAndLinksReversal()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);

        var (reversal, error) = await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Wrong amount");

        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.Equal(JournalEntryStatus.Correction, reversal!.Status);
        Assert.Equal(posted.Id, reversal.SourceJournalEntryId);

        var reloadedOriginal = await _f.Db.JournalEntries.FindAsync(posted.Id);
        Assert.Equal(JournalEntryStatus.Reversed, reloadedOriginal!.Status);
    }

    [Fact]
    public async Task CreateReversalAsync_AlreadyReversedEntry_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);
        await _f.JournalEntryService.CreateReversalAsync(posted.Id, "First reversal");

        var (secondReversal, error) = await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Second attempt");

        Assert.Null(secondReversal);
        Assert.NotNull(error);
        Assert.Contains("already been reversed", error, StringComparison.OrdinalIgnoreCase);
    }
}
