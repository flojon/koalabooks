using KoalaBooks.Application.Services;

namespace KoalaBooks.Tests;

public class FiscalYearSelectionContextTests
{
    [Fact]
    public void NewContext_HasNoSelection()
    {
        var ctx = new FiscalYearSelectionContext();

        Assert.Null(ctx.LastSelectedFiscalYearId);
    }

    [Fact]
    public void Set_ThenRead_ReturnsTheValueThatWasSet()
    {
        var ctx = new FiscalYearSelectionContext();

        ctx.Set(42);

        Assert.Equal(42, ctx.LastSelectedFiscalYearId);
    }

    [Fact]
    public void Set_Twice_LatestValueWins()
    {
        var ctx = new FiscalYearSelectionContext();

        ctx.Set(1);
        ctx.Set(2);

        Assert.Equal(2, ctx.LastSelectedFiscalYearId);
    }
}
