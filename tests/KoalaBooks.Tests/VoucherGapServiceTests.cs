using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class VoucherGapServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;
    private readonly VoucherGapService _service;

    public VoucherGapServiceTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
        _service = new VoucherGapService(_f.Db);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task FindGapsAsync_NoEntries_ReturnsEmpty()
    {
        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Empty(gaps);
    }

    [Fact]
    public async Task FindGapsAsync_ConsecutiveEntries_ReturnsEmpty()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 100m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 200m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 300m);

        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Empty(gaps);
    }

    [Fact]
    public async Task FindGapsAsync_DeletedMiddleDraft_ReturnsGap()
    {
        var (created1, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        var (created2, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        var (created3, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m));
        Assert.Equal(2, created2!.EntryNumber);

        await _f.JournalEntryService.DeleteDraftAsync(created2.Id);

        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Equal([2], gaps);
    }

    [Fact]
    public async Task FindGapsAsync_MultipleGaps_ReturnsAllMissingInOrder()
    {
        for (var i = 0; i < 5; i++)
            await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m + i));

        var toDelete = await _f.Db.JournalEntries
            .Where(j => j.FiscalYearId == _fy.Id && (j.EntryNumber == 2 || j.EntryNumber == 4))
            .ToListAsync();
        foreach (var entry in toDelete)
            await _f.JournalEntryService.DeleteDraftAsync(entry.Id);

        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Equal([2, 4], gaps);
    }

    [Fact]
    public async Task GetUnexplainedGapsAsync_NoExplanations_ReturnsAllGaps()
    {
        await SeedGapOfTwoAsync();

        var unexplained = await _service.GetUnexplainedGapsAsync(_fy.Id);
        Assert.Equal([2], unexplained);
    }

    [Fact]
    public async Task GetUnexplainedGapsAsync_ExplainedGap_ExcludesIt()
    {
        await SeedGapOfTwoAsync();
        var error = await _service.AddExplanationAsync(_fy.Id, 2, "Utkast makulerat.", "jonas@floden.co");
        Assert.Null(error);

        var unexplained = await _service.GetUnexplainedGapsAsync(_fy.Id);
        Assert.Empty(unexplained);
    }

    [Fact]
    public async Task AddExplanationAsync_NumberIsNotAGap_ReturnsError()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 100m);

        var error = await _service.AddExplanationAsync(_fy.Id, 1, "Not a gap", "jonas@floden.co");

        Assert.NotNull(error);
        Assert.Contains("not a gap", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddExplanationAsync_EmptyExplanation_ReturnsError()
    {
        await SeedGapOfTwoAsync();

        var error = await _service.AddExplanationAsync(_fy.Id, 2, "   ", "jonas@floden.co");

        Assert.NotNull(error);
        Assert.Contains("explanation is required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddExplanationAsync_CalledTwiceForSameGap_UpdatesInPlace()
    {
        await SeedGapOfTwoAsync();
        await _service.AddExplanationAsync(_fy.Id, 2, "First reason", "jonas@floden.co");

        var error = await _service.AddExplanationAsync(_fy.Id, 2, "Corrected reason", "jonas@floden.co");
        Assert.Null(error);

        var explanations = await _service.GetExplanationsAsync(_fy.Id);
        var single = Assert.Single(explanations);
        Assert.Equal("Corrected reason", single.Explanation);
    }

    private async Task SeedGapOfTwoAsync()
    {
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        var (created2, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m));

        await _f.JournalEntryService.DeleteDraftAsync(created2!.Id);
    }
}
