using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// Verifies that multi-tenant query filters prevent cross-tenant data access.
/// Each test seeds data under Org A, then queries via Org B and asserts isolation.
/// </summary>
public class TenantIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly int _orgAId;
    private readonly int _orgBId;

    public TenantIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var bootstrap = new AppDbContext(_options, NoTenant());
        bootstrap.Database.EnsureCreated();
        bootstrap.Organisations.AddRange(
            new Organisation { Name = "Org A", Slug = "org-a" },
            new Organisation { Name = "Org B", Slug = "org-b" });
        bootstrap.SaveChanges();
        _orgAId = bootstrap.Organisations.First(o => o.Slug == "org-a").Id;
        _orgBId = bootstrap.Organisations.First(o => o.Slug == "org-b").Id;
    }

    public void Dispose() => _connection.Dispose();

    // ── FiscalYear ─────────────────────────────────────────────────

    [Fact]
    public async Task GetFiscalYearById_AsOtherTenant_ReturnsNull()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");

        using var dbB = DbFor(_orgBId);
        var result = await dbB.FiscalYears.FirstOrDefaultAsync(f => f.Id == fyA.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllFiscalYears_AsOtherTenant_ReturnsEmpty()
    {
        SeedFiscalYear(_orgAId, "2026");

        using var dbB = DbFor(_orgBId);
        var fiscalYearService = new FiscalYearService(dbB, TestFixture.MakeTenant(_orgBId));
        var results = await fiscalYearService.GetAllAsync();

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetFiscalYearById_ViaService_AsOtherTenant_ReturnsNull()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");

        using var dbB = DbFor(_orgBId);
        var service = new FiscalYearService(dbB, TestFixture.MakeTenant(_orgBId));
        var result = await service.GetByIdAsync(fyA.Id);

        Assert.Null(result);
    }

    // ── Account ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccountById_AsOtherTenant_ReturnsNull()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        var accountA = SeedAccount(fyA.Id, "1910", "Kassa");

        using var dbB = DbFor(_orgBId);
        var service = new AccountService(dbB);
        var result = await service.GetByIdAsync(accountA.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAccounts_AsOtherTenant_ReturnsEmpty()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        SeedAccount(fyA.Id, "1910", "Kassa");

        using var dbB = DbFor(_orgBId);
        var service = new AccountService(dbB);
        var results = await service.GetAllAsync(fyA.Id);

        Assert.Empty(results);
    }

    // ── JournalEntry ───────────────────────────────────────────────

    [Fact]
    public async Task GetJournalEntriesByFiscalYear_AsOtherTenant_ReturnsEmpty()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        var accountA = SeedAccount(fyA.Id, "1910", "Kassa");
        SeedJournalEntry(fyA.Id, accountA.Id);

        using var dbB = DbFor(_orgBId);
        var service = new JournalEntryService(dbB);
        var results = await service.GetByFiscalYearAsync(fyA.Id);

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetJournalEntryById_AsOtherTenant_ReturnsNull()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        var accountA = SeedAccount(fyA.Id, "1910", "Kassa");
        var entryA = SeedJournalEntry(fyA.Id, accountA.Id);

        using var dbB = DbFor(_orgBId);
        var result = await dbB.JournalEntries.FirstOrDefaultAsync(j => j.Id == entryA.Id);

        Assert.Null(result);
    }

    // ── BankTransaction ────────────────────────────────────────────

    [Fact]
    public async Task GetBankTransaction_AsOtherTenant_ReturnsNull()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        var accountA = SeedAccount(fyA.Id, "1910", "Kassa");
        var txnA = SeedBankTransaction(_orgAId, accountA.Id);

        using var dbB = DbFor(_orgBId);
        var result = await dbB.BankTransactions.FirstOrDefaultAsync(b => b.Id == txnA.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllBankTransactions_AsOtherTenant_ReturnsEmpty()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        var accountA = SeedAccount(fyA.Id, "1910", "Kassa");
        SeedBankTransaction(_orgAId, accountA.Id);

        using var dbB = DbFor(_orgBId);
        var results = await dbB.BankTransactions.ToListAsync();

        Assert.Empty(results);
    }

    // ── SupplierInvoice ────────────────────────────────────────────

    [Fact]
    public async Task GetSupplierInvoice_AsOtherTenant_ReturnsNull()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        var invoiceA = SeedSupplierInvoice(fyA.Id);

        using var dbB = DbFor(_orgBId);
        var result = await dbB.SupplierInvoices.FirstOrDefaultAsync(s => s.Id == invoiceA.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllSupplierInvoices_AsOtherTenant_ReturnsEmpty()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        SeedSupplierInvoice(fyA.Id);

        using var dbB = DbFor(_orgBId);
        var results = await dbB.SupplierInvoices.ToListAsync();

        Assert.Empty(results);
    }

    // ── Attachment ─────────────────────────────────────────────────

    [Fact]
    public async Task GetAttachment_AsOtherTenant_ReturnsNull()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");
        var accountA = SeedAccount(fyA.Id, "1910", "Kassa");
        var entryA = SeedJournalEntry(fyA.Id, accountA.Id);
        var attachmentA = SeedAttachment(entryA.Id);

        using var dbB = DbFor(_orgBId);
        var service = new AttachmentService(dbB);
        var result = await service.GetAsync(attachmentA.Id);

        Assert.Null(result);
    }

    // ── OwnTenant can still read its own data ──────────────────────

    [Fact]
    public async Task GetFiscalYearById_AsSameTenant_ReturnsData()
    {
        var fyA = SeedFiscalYear(_orgAId, "2026");

        using var dbA = DbFor(_orgAId);
        var service = new FiscalYearService(dbA, TestFixture.MakeTenant(_orgAId));
        var result = await service.GetByIdAsync(fyA.Id);

        Assert.NotNull(result);
        Assert.Equal(fyA.Id, result.Id);
    }

    [Fact]
    public async Task TwoTenants_SeeOnlyTheirOwnFiscalYears()
    {
        SeedFiscalYear(_orgAId, "OrgA-2026");
        SeedFiscalYear(_orgBId, "OrgB-2026");

        // Query one context at a time: EF Core's compiled-query cache can
        // conflate tenant filter parameters when two instances of the same
        // context type are alive simultaneously.
        List<FiscalYear> allA;
        using (var dbA = DbFor(_orgAId))
            allA = await new FiscalYearService(dbA, TestFixture.MakeTenant(_orgAId)).GetAllAsync();

        List<FiscalYear> allB;
        using (var dbB = DbFor(_orgBId))
            allB = await new FiscalYearService(dbB, TestFixture.MakeTenant(_orgBId)).GetAllAsync();

        Assert.All(allA, fy => Assert.Equal(_orgAId, fy.OrganisationId));
        Assert.All(allB, fy => Assert.Equal(_orgBId, fy.OrganisationId));
        Assert.DoesNotContain(allA, fy => fy.Name == "OrgB-2026");
        Assert.DoesNotContain(allB, fy => fy.Name == "OrgA-2026");
    }

    // ── Helpers ────────────────────────────────────────────────────

    private AppDbContext DbFor(int orgId) =>
        new AppDbContext(_options, TestFixture.MakeTenant(orgId));

    private static TenantContext NoTenant() =>
        new TenantContext(new HttpContextAccessor());

    private FiscalYear SeedFiscalYear(int orgId, string name)
    {
        using var db = DbFor(orgId);
        var fy = new FiscalYear
        {
            Name = name,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            OrganisationId = orgId
        };
        db.FiscalYears.Add(fy);
        db.SaveChanges();
        return fy;
    }

    private Account SeedAccount(int fiscalYearId, string number, string name)
    {
        // Write via the fiscal year's owner org; find the org from the fiscal year.
        using var bootstrap = new AppDbContext(_options, NoTenant());
        var orgId = bootstrap.FiscalYears
            .IgnoreQueryFilters()
            .Where(f => f.Id == fiscalYearId)
            .Select(f => f.OrganisationId)
            .First();

        using var db = DbFor(orgId);
        var account = new Account
        {
            AccountNumber = number,
            Name = name,
            AccountClass = AccountClass.Asset,
            IsActive = true,
            FiscalYearId = fiscalYearId
        };
        db.Accounts.Add(account);
        db.SaveChanges();
        return account;
    }

    private JournalEntry SeedJournalEntry(int fiscalYearId, int accountId)
    {
        using var bootstrap = new AppDbContext(_options, NoTenant());
        var orgId = bootstrap.FiscalYears
            .IgnoreQueryFilters()
            .Where(f => f.Id == fiscalYearId)
            .Select(f => f.OrganisationId)
            .First();

        using var db = DbFor(orgId);
        var entry = new JournalEntry
        {
            EntryNumber = 1,
            Date = new DateOnly(2026, 6, 1),
            Description = "Test entry",
            FiscalYearId = fiscalYearId,
            IsPosted = true,
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new JournalEntryLine { AccountId = accountId, DebitAmount = 100, CreditAmount = 0 },
                new JournalEntryLine { AccountId = accountId, DebitAmount = 0, CreditAmount = 100 }
            ]
        };
        db.JournalEntries.Add(entry);
        db.SaveChanges();
        return entry;
    }

    private BankTransaction SeedBankTransaction(int orgId, int accountId)
    {
        using var db = DbFor(orgId);
        var txn = new BankTransaction
        {
            OrganisationId = orgId,
            AccountId = accountId,
            Date = new DateOnly(2026, 6, 1),
            Amount = 500m,
            Description = "Test transaction"
        };
        db.BankTransactions.Add(txn);
        db.SaveChanges();
        return txn;
    }

    private SupplierInvoice SeedSupplierInvoice(int fiscalYearId)
    {
        using var bootstrap = new AppDbContext(_options, NoTenant());
        var orgId = bootstrap.FiscalYears
            .IgnoreQueryFilters()
            .Where(f => f.Id == fiscalYearId)
            .Select(f => f.OrganisationId)
            .First();

        using var db = DbFor(orgId);
        var invoice = new SupplierInvoice
        {
            FiscalYearId = fiscalYearId,
            SupplierName = "Test Supplier",
            InvoiceDate = new DateOnly(2026, 6, 1),
            DueDate = new DateOnly(2026, 7, 1),
            AmountExclVat = 800m,
            VatAmount = 200m,
            TotalAmount = 1000m
        };
        db.SupplierInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private JournalEntryAttachment SeedAttachment(int journalEntryId)
    {
        using var bootstrap = new AppDbContext(_options, NoTenant());
        var orgId = bootstrap.JournalEntries
            .IgnoreQueryFilters()
            .Include(j => j.FiscalYear)
            .Where(j => j.Id == journalEntryId)
            .Select(j => j.FiscalYear.OrganisationId)
            .First();

        using var db = DbFor(orgId);
        var attachment = new JournalEntryAttachment
        {
            JournalEntryId = journalEntryId,
            FileName = "receipt.pdf",
            ContentType = "application/pdf",
            FileSize = 1024,
            Data = new byte[1024],
            UploadedAt = DateTime.UtcNow
        };
        db.JournalEntryAttachments.Add(attachment);
        db.SaveChanges();
        return attachment;
    }
}
