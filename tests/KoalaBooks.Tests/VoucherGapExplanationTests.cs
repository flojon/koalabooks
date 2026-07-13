using KoalaBooks.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class VoucherGapExplanationTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;

    public VoucherGapExplanationTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task SaveAndReload_RoundTripsAllFields()
    {
        var explanation = new VoucherGapExplanation
        {
            FiscalYearId = _fy.Id,
            MissingEntryNumber = 7,
            Explanation = "Utkast makulerat efter felaktig kontering.",
            ExplainedBy = "jonas@floden.co"
        };

        _f.Db.VoucherGapExplanations.Add(explanation);
        await _f.Db.SaveChangesAsync();

        var reloaded = await _f.Db.VoucherGapExplanations.FirstOrDefaultAsync(v => v.Id == explanation.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(_fy.Id, reloaded!.FiscalYearId);
        Assert.Equal(7, reloaded.MissingEntryNumber);
        Assert.Equal("Utkast makulerat efter felaktig kontering.", reloaded.Explanation);
        Assert.Equal("jonas@floden.co", reloaded.ExplainedBy);
    }

    [Fact]
    public async Task DuplicateFiscalYearAndMissingNumber_ThrowsOnSaveChanges()
    {
        _f.Db.VoucherGapExplanations.Add(new VoucherGapExplanation
        {
            FiscalYearId = _fy.Id,
            MissingEntryNumber = 3,
            Explanation = "First",
            ExplainedBy = "a@example.com"
        });
        await _f.Db.SaveChangesAsync();

        _f.Db.VoucherGapExplanations.Add(new VoucherGapExplanation
        {
            FiscalYearId = _fy.Id,
            MissingEntryNumber = 3,
            Explanation = "Second",
            ExplainedBy = "b@example.com"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _f.Db.SaveChangesAsync());
    }
}
