using KoalaBooks.Application.Jobs;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class TestFixture : IDisposable
{
    private readonly LocalCurrentUser _currentUser;
    private readonly string _dbName;

    public AppDbContext Db { get; }
    public JournalEntryService JournalEntryService { get; }
    public FiscalYearService FiscalYearService { get; }
    public VoucherGapService VoucherGapService { get; }
    public YearEndClosingService YearEndClosingService { get; }
    public SieExportService SieExportService { get; }
    public SieImportService SieImportService { get; }

    public int OrganisationId { get; private set; }

    public TestFixture()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr)
            .Options;

        // OrganisationId starts null so the org INSERT runs without a tenant filter.
        // After the org is created, SetActiveTenant sets the real id so all
        // subsequent service calls and query filters see the correct organisation.
        _currentUser = new LocalCurrentUser();
        Db = new AppDbContext(options, _currentUser);
        Db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        Db.Organisations.Add(org);
        Db.SaveChanges();
        OrganisationId = org.Id;
        SetActiveTenant(OrganisationId);

        JournalEntryService = new JournalEntryService(Db);
        FiscalYearService = new FiscalYearService(Db, _currentUser);
        VoucherGapService = new VoucherGapService(Db);
        YearEndClosingService = new YearEndClosingService(Db, FiscalYearService, VoucherGapService);
        SieExportService = new SieExportService(Db);
        SieImportService = new SieImportService(Db, _currentUser);
    }

    public void SetActiveTenant(int orgId)
    {
        _currentUser.OrganisationId = orgId;
    }

    public void Dispose()
    {
        Db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    // ── Static factories used by TenantIsolationTests ──────────────

    public static ICurrentUser MakeTenant(int orgId) => new LocalCurrentUser(orgId);
    public static ICurrentUser NullTenant() => new LocalCurrentUser();

    // ── Seed data helpers ──────────────────────────────────────────

    public FiscalYear CreateFiscalYear(
        string name = "2026",
        DateOnly? start = null,
        DateOnly? end = null,
        bool isClosed = false)
    {
        var fy = new FiscalYear
        {
            Name = name,
            StartDate = start ?? new DateOnly(2026, 1, 1),
            EndDate = end ?? new DateOnly(2026, 12, 31),
            IsClosed = isClosed,
            OrganisationId = OrganisationId
        };
        Db.FiscalYears.Add(fy);
        Db.SaveChanges();
        return fy;
    }

    public Account CreateAccount(
        int fiscalYearId,
        string number,
        string name,
        AccountClass accountClass = AccountClass.Asset,
        decimal incomingBalance = 0,
        decimal outgoingBalance = 0)
    {
        var account = new Account
        {
            AccountNumber = number,
            Name = name,
            AccountClass = accountClass,
            FiscalYearId = fiscalYearId,
            IncomingBalance = incomingBalance,
            OutgoingBalance = outgoingBalance
        };
        Db.Accounts.Add(account);
        Db.SaveChanges();
        return account;
    }

    public async Task<Account> CreateAccountAsync(
        int fiscalYearId,
        string number,
        string name,
        AccountClass accountClass = AccountClass.Asset,
        decimal incomingBalance = 0,
        decimal outgoingBalance = 0)
    {
        var account = new Account
        {
            AccountNumber = number,
            Name = name,
            AccountClass = accountClass,
            FiscalYearId = fiscalYearId,
            IncomingBalance = incomingBalance,
            OutgoingBalance = outgoingBalance
        };
        Db.Accounts.Add(account);
        await Db.SaveChangesAsync();
        return account;
    }

    public JournalEntry MakeEntry(
        int fiscalYearId,
        int debitAccountId,
        int creditAccountId,
        decimal amount,
        DateOnly? date = null,
        string? description = null)
    {
        return new JournalEntry
        {
            Date = date ?? new DateOnly(2026, 6, 15),
            Description = description ?? $"Test entry {amount}",
            FiscalYearId = fiscalYearId,
            Lines =
            [
                new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
                new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
            ]
        };
    }

    public async Task<JournalEntry> CreateAndPostEntryAsync(
        int fiscalYearId,
        int debitAccountId,
        int creditAccountId,
        decimal amount,
        DateOnly? date = null,
        string? description = null)
    {
        var entry = MakeEntry(fiscalYearId, debitAccountId, creditAccountId, amount, date, description);
        var (created, error) = await JournalEntryService.CreateAsync(entry);
        Assert.Null(error);
        Assert.NotNull(created);
        var postError = await JournalEntryService.PostAsync(created.Id);
        Assert.Null(postError);
        return created;
    }

    public (Account Cash, Account Liability, Account Equity, Account Revenue, Account Expense)
        CreateStandardAccounts(int fiscalYearId)
    {
        var cash = CreateAccount(fiscalYearId, "1910", "Kassa", AccountClass.Asset);
        var liability = CreateAccount(fiscalYearId, "2440", "Leverantörsskulder", AccountClass.Liability);
        var equity = CreateAccount(fiscalYearId, "2081", "Aktiekapital", AccountClass.Equity);
        var revenue = CreateAccount(fiscalYearId, "3010", "Försäljning", AccountClass.Revenue);
        var expense = CreateAccount(fiscalYearId, "5010", "Lokalhyra", AccountClass.Expense);
        return (cash, liability, equity, revenue, expense);
    }

    public DocumentService MakeDocumentService() =>
        MakeDocumentService(new DbDocumentStorage(Db));

    public DocumentService MakeDocumentService(IDocumentStorage storage) =>
        new DocumentService(Db, storage, new NoOpDocumentExtractionQueue(), new NoOpZipImportQueue(), _currentUser);

    public DocumentService MakeDocumentService(IDocumentExtractionQueue extractionQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), extractionQueue, new NoOpZipImportQueue(), _currentUser);

    public DocumentService MakeDocumentService(IZipImportQueue zipImportQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), new NoOpDocumentExtractionQueue(), zipImportQueue, _currentUser);
}
