using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class FiscalYearSelectionContextResolveSeedTests : IDisposable
{
    private readonly TestFixture _f;

    public FiscalYearSelectionContextResolveSeedTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task LastSelectedIdInCandidates_WinsOverDefault()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var candidateYear = _f.CreateFiscalYear("Candidate", today.AddYears(-2), today.AddYears(-2).AddMonths(11));
        var defaultYear = _f.CreateFiscalYear("Default", today.AddMonths(-1), today.AddMonths(1));
        var candidates = new List<FiscalYear> { candidateYear, defaultYear };
        var ctx = new FiscalYearSelectionContext();
        ctx.Set(candidateYear.Id);

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates);

        Assert.NotNull(seed);
        Assert.Equal(candidateYear.Id, seed.Id);
    }

    [Fact]
    public async Task LastSelectedIdNotInCandidates_FallsBackToDefault()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var defaultYear = _f.CreateFiscalYear("Default", today.AddMonths(-1), today.AddMonths(1));
        var candidates = new List<FiscalYear> { defaultYear };
        var ctx = new FiscalYearSelectionContext();
        ctx.Set(999999); // stale id, not present in candidates

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates);

        Assert.NotNull(seed);
        Assert.Equal(defaultYear.Id, seed.Id);
    }

    [Fact]
    public async Task NoDefault_UsesExtraFallback()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Both years are closed and neither covers "today", so GetDefaultFiscalYearAsync
        // returns null (no year covers today, no open years exist) and the extension
        // must fall through to extraFallback instead of candidates.FirstOrDefault().
        var closedYear = _f.CreateFiscalYear("Closed", today.AddYears(-3), today.AddYears(-3).AddMonths(11), isClosed: true);
        var fallbackYear = _f.CreateFiscalYear("Fallback", today.AddYears(-2), today.AddYears(-2).AddMonths(11), isClosed: true);
        var candidates = new List<FiscalYear> { closedYear, fallbackYear };
        var ctx = new FiscalYearSelectionContext();

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates, extraFallback: fallbackYear);

        Assert.NotNull(seed);
        Assert.Equal(fallbackYear.Id, seed.Id);
    }

    [Fact]
    public async Task NoDefaultNoExtraFallback_FallsBackToFirstCandidate()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var closedYear = _f.CreateFiscalYear("Closed", today.AddYears(-3), today.AddYears(-3).AddMonths(11), isClosed: true);
        var candidates = new List<FiscalYear> { closedYear };
        var ctx = new FiscalYearSelectionContext();

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates);

        Assert.NotNull(seed);
        Assert.Equal(closedYear.Id, seed.Id);
    }
}
