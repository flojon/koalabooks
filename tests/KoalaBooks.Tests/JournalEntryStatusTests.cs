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
}
