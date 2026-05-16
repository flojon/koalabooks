using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace KoalaBooks.Tests;

/// <summary>
/// Shared test fixture that eliminates duplicated SQLite in-memory DB setup
/// across test classes. Provides common seed data helpers and service instances.
/// </summary>
public class TestFixture : IDisposable
{
    public int TestOrgId { get; }

    public AppDbContext Db { get; }
    public TenantContext Tenant { get; }
    public JournalEntryService JournalEntryService { get; }
    public FiscalYearService FiscalYearService { get; }
    public YearEndClosingService YearEndClosingService { get; }
    public SieExportService SieExportService { get; }
    public Organisation DefaultOrg { get; }

    private readonly SqliteConnection _connection;

    public TestFixture()
    {
        // Keep a single open connection so the in-memory database persists
        // across multiple AppDbContext instances in the same test.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Bootstrap: create schema and seed Organisation without a tenant filter.
        Organisation bootstrapOrg;
        var noTenant = MakeNullTenant();
        using (var bootstrap = new AppDbContext(options, noTenant))
        {
            bootstrap.Database.EnsureCreated();
            bootstrapOrg = new Organisation { Name = "Test Org", Slug = "test-org" };
            bootstrap.Organisations.Add(bootstrapOrg);
            bootstrap.SaveChanges();
            TestOrgId = bootstrapOrg.Id;
        }

        DefaultOrg = bootstrapOrg;
        Tenant = MakeTenant(TestOrgId);
        Db = new AppDbContext(options, Tenant);

        JournalEntryService = new JournalEntryService(Db);
        FiscalYearService = new FiscalYearService(Db, Tenant);
        YearEndClosingService = new YearEndClosingService(Db, FiscalYearService);
        SieExportService = new SieExportService(Db);
    }

    public static TenantContext MakeTenant(int orgId)
    {
        var claims = new[] { new Claim("org_id", orgId.ToString()) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var ctx = new DefaultHttpContext { User = principal };
        return new TenantContext(new HttpContextAccessor { HttpContext = ctx });
    }

    private static TenantContext MakeNullTenant() =>
        new TenantContext(new HttpContextAccessor());

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }

    internal static TenantContext NullTenant() => new TenantContext(new HttpContextAccessor());

    // ── Seed data helpers ──────────────────────────────────────────

    public FiscalYear CreateFiscalYear(
        string name = "2026",
        DateOnly? start = null,
        DateOnly? end = null,
        bool isClosed = false,
        int? organisationId = null)
    {
        var fy = new FiscalYear
        {
            Name = name,
            StartDate = start ?? new DateOnly(2026, 1, 1),
            EndDate = end ?? new DateOnly(2026, 12, 31),
            IsClosed = isClosed,
            OrganisationId = organisationId ?? TestOrgId
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
