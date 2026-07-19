using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class VoucherGapClosingValidationTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public VoucherGapClosingValidationTests()
    {
        _f = new TestFixture();
        // EndDate must be in the past — closing before a year has ended is now rejected (issue #307).
        _fy = _f.CreateFiscalYear(end: DateOnly.FromDateTime(DateTime.Today).AddDays(-1));
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    private async Task<int> SeedUnexplainedGapAsync()
    {
        var (created1, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        var (created2, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        var (created3, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m));

        await _f.JournalEntryService.DeleteDraftAsync(created2!.Id);
        await _f.JournalEntryService.PostAsync(created1!.Id);
        await _f.JournalEntryService.PostAsync(created3!.Id);

        return created2.EntryNumber; // 2
    }

    [Fact]
    public async Task ValidateForClosingAsync_UnexplainedGap_ReturnsError()
    {
        await SeedUnexplainedGapAsync();

        var result = await _f.YearEndClosingService.ValidateForClosingAsync(_fy.Id);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BFNAR 2013:2"));
    }

    [Fact]
    public async Task ValidateForClosingAsync_ExplainedGap_NoGapError()
    {
        var missingNumber = await SeedUnexplainedGapAsync();
        var gapError = await _f.VoucherGapService.AddExplanationAsync(
            _fy.Id, missingNumber, "Utkast makulerat efter felkontering.", "jonas@floden.co");
        Assert.Null(gapError);

        var result = await _f.YearEndClosingService.ValidateForClosingAsync(_fy.Id);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Contains("BFNAR 2013:2"));
    }

    [Fact]
    public async Task ExecuteClosingAsync_UnexplainedGap_FiscalYearStaysOpen()
    {
        await SeedUnexplainedGapAsync();

        var result = await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);

        Assert.False(result.Success);
        Assert.Contains("BFNAR 2013:2", result.Error);

        var reloaded = await _f.Db.FiscalYears.FindAsync(_fy.Id);
        Assert.False(reloaded!.IsClosed);
    }
}
