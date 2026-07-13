using KoalaBooks.Application.Services;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Demonstrates what's needed to bUnit-test a page built on MudBlazor: unlike the
// plain Shared/ components, MudAlert et al. need Services.AddMudServices() plus
// JSInterop in loose mode (popover/resize-observer/key-interceptor calls are
// otherwise unconfigured). The bigger friction here isn't MudBlazor itself though —
// it's that Home.razor injects concrete EF-backed services (FiscalYearService,
// JournalEntryService) with no interface seam, so an EF Core InMemory AppDbContext
// is wired up rather than a fake.
public class HomeTests : BunitContext, IDisposable
{
    private readonly AppDbContext _db;
    private readonly LocalCurrentUser _currentUser;

    public HomeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _currentUser.OrganisationId = org.Id;

        Services.AddSingleton(_db);
        Services.AddSingleton<ICurrentUser>(_currentUser);
        Services.AddSingleton<FiscalYearService>();
        Services.AddSingleton<JournalEntryService>();
    }

    [Fact]
    public void NoActiveFiscalYear_ShowsInfoAlert()
    {
        var cut = Render<Home>();

        Assert.Contains("Inget aktivt räkenskapsår hittades", cut.Markup);
    }

    [Fact]
    public void ActiveFiscalYear_ShowsNameAndDashboardStats()
    {
        var fiscalYear = new FiscalYear
        {
            OrganisationId = _currentUser.OrganisationId!.Value,
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
        };
        _db.FiscalYears.Add(fiscalYear);
        _db.SaveChanges();

        var cut = Render<Home>();

        Assert.Contains("2026", cut.Markup);
        Assert.DoesNotContain("Inget aktivt räkenskapsår hittades", cut.Markup);
    }

    public new void Dispose()
    {
        _db.Dispose();
        base.Dispose();
    }
}
