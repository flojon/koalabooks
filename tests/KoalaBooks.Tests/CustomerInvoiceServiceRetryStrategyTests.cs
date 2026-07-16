using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class CustomerInvoiceServiceRetryStrategyTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly LocalCurrentUser _currentUser;
    private readonly int _organisationId;
    private readonly int _fiscalYearId;

    public CustomerInvoiceServiceRetryStrategyTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        // Mirrors Program.cs's EnrichNpgsqlDbContext, which enables a retrying
        // execution strategy in the real app — CreateAsync's manual transaction
        // must be compatible with it (see DbDocumentStorageRetryStrategyTests).
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _organisationId = org.Id;
        _currentUser.OrganisationId = _organisationId;

        var fiscalYear = new FiscalYear
        {
            OrganisationId = _organisationId,
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
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
    public async Task CreateAsync_SucceedsUnderRetryingExecutionStrategy()
    {
        var service = new CustomerInvoiceService(_db);
        var invoice = new CustomerInvoice
        {
            FiscalYearId = _fiscalYearId,
            CustomerName = "Test Customer",
            InvoiceDate = new DateOnly(2026, 7, 15),
            DueDate = new DateOnly(2026, 8, 14),
        };
        var lines = new List<CustomerInvoiceLine>
        {
            new() { Description = "Widget", Quantity = 1, UnitPrice = 100, VatRate = 25 },
        };

        var (created, error) = await service.CreateAsync(invoice, lines);

        Assert.Null(error);
        Assert.NotNull(created);
        Assert.Equal(1, created!.InvoiceNumber);
    }

    [Fact]
    public async Task CreateAsync_AssignsSequentialInvoiceNumbersAcrossCalls()
    {
        var service = new CustomerInvoiceService(_db);
        var lines = new List<CustomerInvoiceLine>
        {
            new() { Description = "Widget", Quantity = 1, UnitPrice = 100, VatRate = 25 },
        };

        var (first, _) = await service.CreateAsync(new CustomerInvoice
        {
            FiscalYearId = _fiscalYearId,
            CustomerName = "Customer A",
            InvoiceDate = new DateOnly(2026, 7, 15),
            DueDate = new DateOnly(2026, 8, 14),
        }, lines);

        var (second, _) = await service.CreateAsync(new CustomerInvoice
        {
            FiscalYearId = _fiscalYearId,
            CustomerName = "Customer B",
            InvoiceDate = new DateOnly(2026, 7, 15),
            DueDate = new DateOnly(2026, 8, 14),
        }, lines);

        Assert.Equal(1, first!.InvoiceNumber);
        Assert.Equal(2, second!.InvoiceNumber);
    }
}
