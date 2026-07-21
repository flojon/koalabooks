using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.ComponentTests;

public class FiscalYearSelectorTests : BunitContext
{
    private static readonly FiscalYear Fy2025 = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31) };
    private static readonly FiscalYear Fy2026 = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) };

    [Fact]
    public void RendersOneOptionPerFiscalYear()
    {
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id));

        var options = cut.FindAll("option");

        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void DefaultWidth_Is200px()
    {
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id));

        Assert.Contains("width:200px", cut.Find("select").GetAttribute("style"));
    }

    [Fact]
    public void ExplicitWidth_OverridesDefault()
    {
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id)
            .Add(c => c.Width, "220px"));

        Assert.Contains("width:220px", cut.Find("select").GetAttribute("style"));
    }

    [Fact]
    public void ChangingSelection_InvokesCallbackWithNewId()
    {
        int? received = null;
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id)
            .Add(c => c.SelectedFiscalYearIdChanged, (int id) => received = id));

        cut.Find("select").Change(Fy2025.Id.ToString());

        Assert.Equal(Fy2025.Id, received);
    }
}
