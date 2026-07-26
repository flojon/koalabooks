using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class JournalEntryPaginationServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public JournalEntryPaginationServiceTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task GetByFiscalYearAsync_PageSizeSmallerThanTotal_NeverReturnsMoreThanPageSize()
    {
        for (var i = 0; i < 7; i++)
            await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 100m);

        var result = await _f.JournalEntryService.GetByFiscalYearAsync(_fy.Id, page: 1, pageSize: 3);

        Assert.Equal(7, result.TotalCount);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public async Task GetByFiscalYearAsync_IncludesDraftEntries()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 100m, description: "Posted");
        var draft = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 50m, description: "Draft");
        var (created, error) = await _f.JournalEntryService.CreateAsync(draft);
        Assert.Null(error);
        Assert.NotNull(created);

        var result = await _f.JournalEntryService.GetByFiscalYearAsync(_fy.Id, page: 1, pageSize: 10);

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, e => e.Id == created!.Id && !e.IsPosted);
    }
}
