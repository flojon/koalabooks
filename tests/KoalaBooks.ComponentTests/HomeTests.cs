using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// With FiscalYearService/JournalEntryService now behind interfaces, this no longer
// needs a real (even in-memory) AppDbContext - a fake is enough. MudBlazor is still
// the one line of ceremony (Services.AddMudServices() + loose JSInterop) needed to
// render MudAlert without unconfigured JS-interop calls blowing up.
public class HomeTests : BunitContext
{
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IJournalEntryReportingService _journalReportingService = Substitute.For<IJournalEntryReportingService>();

    public HomeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_journalReportingService);
    }

    [Fact]
    public void NoActiveFiscalYear_ShowsInfoAlert()
    {
        _fiscalYearService.GetActiveAsync().Returns((FiscalYear?)null);

        var cut = Render<Home>();

        Assert.Contains("Inget aktivt räkenskapsår hittades", cut.Markup);
    }

    [Fact]
    public void ActiveFiscalYear_ShowsNameAndDashboardStats()
    {
        var fiscalYear = new FiscalYear
        {
            Id = 1,
            OrganisationId = 1,
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
        };
        _fiscalYearService.GetActiveAsync().Returns(fiscalYear);
        _journalReportingService.GetDashboardStatsAsync(1).Returns(new DashboardStats
        {
            EntryCount = 5,
            TotalDebit = 1000m,
            TotalCredit = 1000m,
        });

        var cut = Render<Home>();

        Assert.Contains("2026", cut.Markup);
        Assert.Equal("5", cut.FindAll(".stat-value")[0].TextContent);
        Assert.DoesNotContain("Inget aktivt räkenskapsår hittades", cut.Markup);
    }
}
