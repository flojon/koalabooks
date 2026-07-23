using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

namespace KoalaBooks.Tests.Services;

public class CustomerInvoiceServiceGetPdfTests : IDisposable
{
    static CustomerInvoiceServiceGetPdfTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly LocalCurrentUser _currentUser;
    private readonly int _fiscalYearId;

    public CustomerInvoiceServiceGetPdfTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org-inv-pdf" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _currentUser.OrganisationId = org.Id;

        var fiscalYear = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(fiscalYear);
        _db.SaveChanges();
        _fiscalYearId = fiscalYear.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    [Fact]
    public async Task GetPdfAsync_ReturnsNonEmptyPdfBytes_ForExistingInvoice()
    {
        var service = new CustomerInvoiceService(_db);
        var (invoice, error) = await service.CreateAsync(
            new CustomerInvoice
            {
                FiscalYearId = _fiscalYearId,
                CustomerName = "Acme AB",
                InvoiceDate = new DateOnly(2026, 7, 1),
                DueDate = new DateOnly(2026, 7, 31),
            },
            [new CustomerInvoiceLine { Description = "Konsulttjänst", Quantity = 1, UnitPrice = 1000, VatRate = 25 }]);
        Assert.Null(error);

        var bytes = await service.GetPdfAsync(invoice!.Id);

        Assert.NotNull(bytes);
        Assert.Equal("%PDF"u8.ToArray(), bytes!.Take(4).ToArray());
    }

    [Fact]
    public async Task GetPdfAsync_ReturnsNull_ForUnknownId()
    {
        var service = new CustomerInvoiceService(_db);
        var bytes = await service.GetPdfAsync(999999);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task GetPdfAsync_ReturnsNull_ForCrossTenantInvoice()
    {
        var otherOrg = new Organisation { Name = "Other Org", Slug = "other-org-inv-pdf" };
        _db.Organisations.Add(otherOrg);
        await _db.SaveChangesAsync();

        var otherFiscalYear = new FiscalYear
        {
            OrganisationId = otherOrg.Id, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(otherFiscalYear);
        await _db.SaveChangesAsync();

        _currentUser.OrganisationId = otherOrg.Id;
        var (otherOrgInvoice, error) = await new CustomerInvoiceService(_db).CreateAsync(
            new CustomerInvoice
            {
                FiscalYearId = otherFiscalYear.Id,
                CustomerName = "Other Acme AB",
                InvoiceDate = new DateOnly(2026, 7, 1),
                DueDate = new DateOnly(2026, 7, 31),
            },
            [new CustomerInvoiceLine { Description = "Konsulttjänst", Quantity = 1, UnitPrice = 1000, VatRate = 25 }]);
        Assert.Null(error);

        _currentUser.OrganisationId = _db.Organisations.Single(o => o.Slug == "test-org-inv-pdf").Id;
        var service = new CustomerInvoiceService(_db);
        var bytes = await service.GetPdfAsync(otherOrgInvoice!.Id);

        Assert.Null(bytes);
    }
}
