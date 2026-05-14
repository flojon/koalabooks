using System.Security.Claims;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// Shared test fixture that eliminates duplicated SQLite in-memory DB setup
/// across test classes. Provides common seed data helpers and service instances.
/// </summary>
public class TestFixture : IDisposable
{
    public AppDbContext Db { get; }
    public JournalEntryService JournalEntryService { get; }
    public FiscalYearService FiscalYearService { get; }
    public YearEndClosingService YearEndClosingService { get; }
    public SieExportService SieExportService { get; }
    public SieImportService SieImportService { get; }

    public int OrganisationId { get; private set; }

    public TestFixture()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        // Start with null HttpContext so the initial SaveChanges (creating the org) has no filter applied.
        // After the org is created, wire up a real HttpContext with the org_id claim so that
        // FiscalYearService.CreateAsync and query filters work correctly for all subsequent operations.
        var accessor = new HttpContextAccessor();
        var tenant = new TenantContext(accessor);
        Db = new AppDbContext(options, tenant);
        Db.Database.OpenConnection();
        Db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org" };
        Db.Organisations.Add(org);
        Db.SaveChanges();
        OrganisationId = org.Id;

        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("org_id", OrganisationId.ToString())]))
        };

        JournalEntryService = new JournalEntryService(Db);
        FiscalYearService = new FiscalYearService(Db, tenant);
        YearEndClosingService = new YearEndClosingService(Db, FiscalYearService);
        SieExportService = new SieExportService(Db);
        SieImportService = new SieImportService(Db, tenant);
    }

    public void Dispose() => Db.Dispose();

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

    /// <summary>
    /// Creates a balanced journal entry with two lines (debit/credit).
    /// </summary>
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

    /// <summary>
    /// Creates and posts a balanced journal entry. Asserts no errors.
    /// </summary>
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

    /// <summary>
    /// Creates a standard set of accounts for a fiscal year.
    /// Returns (cash, liability, equity, revenue, expense).
    /// </summary>
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
}
