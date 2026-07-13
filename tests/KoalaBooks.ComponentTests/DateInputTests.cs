using KoalaBooks.Components.Shared;

namespace KoalaBooks.ComponentTests;

public class DateInputTests : BunitContext
{
    [Fact]
    public void RendersEmptyInput_WhenValueIsDefault()
    {
        var cut = Render<DateInput>(p => p.Add(x => x.Value, default));

        Assert.Equal("", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void RendersFormattedDate_WhenValueIsSet()
    {
        var cut = Render<DateInput>(p => p.Add(x => x.Value, new DateTime(2026, 3, 5)));

        Assert.Equal("2026-03-05", cut.Find("input").GetAttribute("value"));
    }

    [Fact]
    public void OnChange_WithValidDate_InvokesValueChanged()
    {
        DateTime? changedTo = null;
        var cut = Render<DateInput>(p => p
            .Add(x => x.Value, new DateTime(2026, 1, 1))
            .Add(x => x.ValueChanged, d => changedTo = d));

        cut.Find("input").Change("2026-06-15");

        Assert.Equal(new DateTime(2026, 6, 15), changedTo);
    }

    [Fact]
    public void OnChange_WithInvalidDate_DoesNotInvokeValueChanged()
    {
        DateTime? changedTo = null;
        var cut = Render<DateInput>(p => p
            .Add(x => x.Value, new DateTime(2026, 1, 1))
            .Add(x => x.ValueChanged, d => changedTo = d));

        cut.Find("input").Change("not-a-date");

        Assert.Null(changedTo);
    }

    [Fact]
    public void AdditionalAttributes_ArePassedThrough()
    {
        var cut = Render<DateInput>(p => p
            .Add(x => x.Value, default)
            .AddUnmatched("disabled", "disabled"));

        Assert.True(cut.Find("input").HasAttribute("disabled"));
    }
}
