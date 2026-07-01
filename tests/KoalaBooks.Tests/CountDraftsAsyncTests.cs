using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class CountDraftsAsyncTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public CountDraftsAsyncTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task CountDrafts_NoEntries_ReturnsZero()
    {
        var count = await _f.JournalEntryService.CountDraftsAsync(_fy.Id);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountDrafts_OnlyCountsUnpostedEntries()
    {
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 300m);

        var count = await _f.JournalEntryService.CountDraftsAsync(_fy.Id);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountDrafts_ScopedToFiscalYear()
    {
        var otherFy = _f.CreateFiscalYear("2027", new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var (otherCash, _, _, otherRevenue, _) = _f.CreateStandardAccounts(otherFy.Id);
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(otherFy.Id, otherCash.Id, otherRevenue.Id, 500m));

        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));

        var count = await _f.JournalEntryService.CountDraftsAsync(_fy.Id);

        Assert.Equal(1, count);
    }
}
