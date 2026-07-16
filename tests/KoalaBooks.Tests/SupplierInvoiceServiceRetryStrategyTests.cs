using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class SupplierInvoiceServiceRetryStrategyTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly LocalCurrentUser _currentUser;
    private readonly int _fiscalYearId;
    private readonly int _expenseAccountId;
    private readonly int _payableAccountId;
    private readonly int _vatAccountId;
    private readonly int _bankAccountId;

    public SupplierInvoiceServiceRetryStrategyTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        // Mirrors Program.cs's EnrichNpgsqlDbContext, which enables a retrying
        // execution strategy in the real app — PostAsync/MarkAsPaidAsync's manual
        // transactions must be compatible with it (see DbDocumentStorageRetryStrategyTests).
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _currentUser.OrganisationId = org.Id;

        var fiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
        };
        _db.FiscalYears.Add(fiscalYear);
        _db.SaveChanges();
        _fiscalYearId = fiscalYear.Id;

        Account MakeAccount(string number, string name) => new()
        {
            FiscalYearId = _fiscalYearId,
            AccountNumber = number,
            Name = name
        };
        var expense = MakeAccount("5010", "Inköp material");
        var payable = MakeAccount("2440", "Leverantörsskulder");
        var vat = MakeAccount("2641", "Ingående moms");
        var bank = MakeAccount("1930", "Företagskonto");
        _db.Accounts.AddRange(expense, payable, vat, bank);
        _db.SaveChanges();
        _expenseAccountId = expense.Id;
        _payableAccountId = payable.Id;
        _vatAccountId = vat.Id;
        _bankAccountId = bank.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    [Fact]
    public async Task PostAsync_SucceedsUnderRetryingExecutionStrategy()
    {
        var service = new SupplierInvoiceService(_db);
        var (invoice, createError) = await service.CreateAsync(new SupplierInvoice
        {
            FiscalYearId = _fiscalYearId,
            SupplierName = "Test Supplier",
            InvoiceDate = new DateOnly(2026, 7, 15),
            DueDate = new DateOnly(2026, 8, 14),
            AmountExclVat = 800m,
            VatAmount = 200m,
            TotalAmount = 1000m
        });
        Assert.Null(createError);

        var (posted, postError) = await service.PostAsync(
            invoice!.Id, _expenseAccountId, _payableAccountId, _vatAccountId);

        Assert.Null(postError);
        Assert.NotNull(posted);
        Assert.NotNull(posted!.JournalEntryId);
    }

    [Fact]
    public async Task MarkAsPaidAsync_SucceedsUnderRetryingExecutionStrategy()
    {
        var service = new SupplierInvoiceService(_db);
        var (invoice, createError) = await service.CreateAsync(new SupplierInvoice
        {
            FiscalYearId = _fiscalYearId,
            SupplierName = "Test Supplier",
            InvoiceDate = new DateOnly(2026, 7, 15),
            DueDate = new DateOnly(2026, 8, 14),
            AmountExclVat = 800m,
            VatAmount = 200m,
            TotalAmount = 1000m
        });
        Assert.Null(createError);

        var (_, postError) = await service.PostAsync(
            invoice!.Id, _expenseAccountId, _payableAccountId, _vatAccountId);
        Assert.Null(postError);

        var (paid, payError) = await service.MarkAsPaidAsync(
            invoice.Id, new DateOnly(2026, 7, 20), _bankAccountId, _payableAccountId);

        Assert.Null(payError);
        Assert.NotNull(paid);
        Assert.True(paid!.IsPaid);
        Assert.NotNull(paid.PaymentJournalEntryId);
    }
}
