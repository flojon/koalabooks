using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Application.Services;

public static class DemoDataSeeder
{
    public const string DemoUserEmail = "admin@koalabooks.local";
    public const string DemoUserPassword = "Admin123!";

    // Lets previewers verify admin-gated areas (e.g. /hangfire) reject a non-admin too.
    public const string DemoNonAdminUserEmail = "member@koalabooks.local";
    public const string DemoNonAdminUserPassword = "Member123!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(DemoNonAdminUserEmail) is not null)
            return;

        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        var tenant = new LocalCurrentUser();
        await using var db = new AppDbContext(options, tenant);

        var org = await db.Organisations.FirstOrDefaultAsync(o => o.Slug == "demo");
        if (org is null)
        {
            org = new Organisation { Name = "Demo AB", Slug = "demo", LegalForm = LegalForm.Aktiebolag };
            db.Organisations.Add(org);
            await db.SaveChangesAsync();
        }
        tenant.OrganisationId = org.Id;

        var currentYearNumber = DateTime.UtcNow.Year;
        var previousYearName = (currentYearNumber - 1).ToString();
        var currentYearName = currentYearNumber.ToString();

        var previousFiscalYear = await db.FiscalYears.FirstOrDefaultAsync(f => f.Name == previousYearName);
        var currentFiscalYear = await db.FiscalYears.FirstOrDefaultAsync(f => f.Name == currentYearName);

        // Existence-checked (not just the demo user) so a retry after a partial failure can't add a duplicate pair.
        if (previousFiscalYear is null || currentFiscalYear is null)
        {
            previousFiscalYear = new FiscalYear
            {
                OrganisationId = org.Id,
                Name = previousYearName,
                StartDate = new DateOnly(currentYearNumber - 1, 1, 1),
                EndDate = new DateOnly(currentYearNumber - 1, 12, 31),
                IsClosed = false
            };
            currentFiscalYear = new FiscalYear
            {
                OrganisationId = org.Id,
                Name = currentYearName,
                StartDate = new DateOnly(currentYearNumber, 1, 1),
                EndDate = new DateOnly(currentYearNumber, 12, 31),
                IsClosed = false
            };
            db.FiscalYears.AddRange(previousFiscalYear, currentFiscalYear);
            await db.SaveChangesAsync();

            currentFiscalYear.PreviousFiscalYearId = previousFiscalYear.Id;
            await db.SaveChangesAsync();

            await new BasImportService(db).ImportDefaultAsync(previousFiscalYear.Id);
            await new BasImportService(db).ImportDefaultAsync(currentFiscalYear.Id);

            await SeedPreviousYearEntriesAsync(db, tenant, previousFiscalYear);
            await SeedCurrentYearEntriesAsync(db, currentFiscalYear.Id);
            await SeedCustomersAndInvoicesAsync(db, org.Id, currentFiscalYear.Id);
        }

        // Existence-checked so a retry after a partial failure can't fail as a duplicate.
        if (await userManager.FindByEmailAsync(DemoUserEmail) is null)
        {
            var demoUser = new ApplicationUser
            {
                UserName = DemoUserEmail,
                Email = DemoUserEmail,
                EmailConfirmed = true,
                DisplayName = "Admin",
                OrganisationId = org.Id
            };
            var createResult = await userManager.CreateAsync(demoUser, DemoUserPassword);
            if (!createResult.Succeeded)
                throw new InvalidOperationException(
                    $"Failed to create demo user: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
        }

        // Created last so its existence is a true "fully seeded" marker for the idempotency guard above.
        var demoNonAdminUser = new ApplicationUser
        {
            UserName = DemoNonAdminUserEmail,
            Email = DemoNonAdminUserEmail,
            EmailConfirmed = true,
            DisplayName = "Member",
            OrganisationId = org.Id
        };
        var nonAdminCreateResult = await userManager.CreateAsync(demoNonAdminUser, DemoNonAdminUserPassword);
        if (!nonAdminCreateResult.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create demo non-admin user: {string.Join("; ", nonAdminCreateResult.Errors.Select(e => e.Description))}");
    }

    private static async Task<Dictionary<string, Account>> LoadDemoAccountsAsync(AppDbContext db, int fiscalYearId)
    {
        return await db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Where(a => a.AccountNumber == "1910" || a.AccountNumber == "2440"
                || a.AccountNumber == "2081" || a.AccountNumber == "3001" || a.AccountNumber == "5010"
                || a.AccountNumber == "2641")
            .ToDictionaryAsync(a => a.AccountNumber);
    }

    private static async Task PostEntryAsync(
        JournalEntryService journalEntryService, int fiscalYearId, DateOnly date,
        int debitAccountId, int creditAccountId, decimal amount, string description)
    {
        var entry = new JournalEntry
        {
            Date = date,
            Description = description,
            FiscalYearId = fiscalYearId,
            Lines =
            [
                new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
                new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
            ]
        };

        var (created, error) = await journalEntryService.CreateAsync(entry);
        if (error is not null)
            throw new InvalidOperationException($"Demo seed failed to create journal entry '{description}': {error}");

        var postError = await journalEntryService.PostAsync(created!.Id);
        if (postError is not null)
            throw new InvalidOperationException($"Demo seed failed to post journal entry '{description}': {postError}");
    }

    private static async Task SeedPreviousYearEntriesAsync(AppDbContext db, LocalCurrentUser tenant, FiscalYear previousFiscalYear)
    {
        var accounts = await LoadDemoAccountsAsync(db, previousFiscalYear.Id);
        var cash = accounts["1910"].Id;
        var payables = accounts["2440"].Id;
        var revenue = accounts["3001"].Id;
        var expense = accounts["5010"].Id;

        var year = previousFiscalYear.StartDate.Year;
        var journalEntryService = new JournalEntryService(db);

        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 2, 10), cash, revenue, 9000m, "Kontantförsäljning");
        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 5, 15), expense, cash, 8000m, "Lokalhyra");
        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 8, 20), cash, revenue, 11000m, "Kontantförsäljning");
        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 11, 5), expense, payables, 4000m, "Inköp material");

        // Close via the real service so it posts closing entries and propagates balances forward; wrap in the execution strategy since it opens its own transaction.
        var fiscalYearService = new FiscalYearService(db, tenant);
        var voucherGapService = new VoucherGapService(db);
        var closingService = new YearEndClosingService(db, fiscalYearService, voucherGapService);
        var strategy = db.Database.CreateExecutionStrategy();
        var closingResult = await strategy.ExecuteAsync(() => closingService.ExecuteClosingAsync(previousFiscalYear.Id));
        if (!closingResult.Success)
            throw new InvalidOperationException($"Demo seed failed to close previous fiscal year: {closingResult.Error}");
    }

    private static async Task SeedCurrentYearEntriesAsync(AppDbContext db, int fiscalYearId)
    {
        var accounts = await LoadDemoAccountsAsync(db, fiscalYearId);
        var cash = accounts["1910"].Id;
        var payables = accounts["2440"].Id;
        var equity = accounts["2081"].Id;
        var revenue = accounts["3001"].Id;
        var expense = accounts["5010"].Id;

        var year = DateTime.UtcNow.Year;
        var journalEntryService = new JournalEntryService(db);

        (DateOnly Date, int DebitAccountId, int CreditAccountId, decimal Amount, string Description)[] entries =
        [
            (new DateOnly(year, 1, 5), cash, equity, 50000m, "Aktiekapital"),
            (new DateOnly(year, 2, 10), cash, revenue, 12000m, "Kontantförsäljning"),
            (new DateOnly(year, 3, 1), expense, cash, 8000m, "Lokalhyra"),
            (new DateOnly(year, 4, 15), cash, revenue, 15000m, "Kontantförsäljning"),
            (new DateOnly(year, 5, 20), payables, cash, 3000m, "Betalning leverantörsfaktura"),
            (new DateOnly(year, 6, 10), expense, payables, 4500m, "Inköp material")
        ];

        foreach (var (date, debitAccountId, creditAccountId, amount, description) in entries)
        {
            await PostEntryAsync(journalEntryService, fiscalYearId, date, debitAccountId, creditAccountId, amount, description);
        }

        // Bypass JournalEntryService and the ChangeTracker (via ExecuteDelete) to delete voucher #3,
        // simulating the historical direct-DB deletion gap detection is meant to catch.
        var postedEntryIds = await db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .OrderBy(j => j.EntryNumber)
            .Select(j => j.Id)
            .ToListAsync();

        var gapEntryId = postedEntryIds[2];
        await db.JournalEntryLines.Where(l => l.JournalEntryId == gapEntryId).ExecuteDeleteAsync();
        await db.JournalEntries.Where(j => j.Id == gapEntryId).ExecuteDeleteAsync();
    }

    private static async Task SeedCustomersAndInvoicesAsync(AppDbContext db, int organisationId, int fiscalYearId)
    {
        var customerService = new CustomerService(db);
        var customers = new[]
        {
            new Customer { OrganisationId = organisationId, Name = "Nordic Design AB", OrgNumber = "556677-8899", Email = "info@nordicdesign.se" },
            new Customer { OrganisationId = organisationId, Name = "Café Solglimt", OrgNumber = "556211-3344", Email = "kontakt@cafesolglimt.se" },
            new Customer { OrganisationId = organisationId, Name = "Björk & Partners HB", OrgNumber = "969712-5566", Email = "info@bjorkpartners.se" }
        };

        Customer? nordicDesign = null;
        foreach (var customer in customers)
        {
            var (created, error) = await customerService.CreateAsync(customer);
            if (error is not null)
                throw new InvalidOperationException($"Demo seed failed to create customer '{customer.Name}': {error}");
            if (customer.Name == "Nordic Design AB")
                nordicDesign = created;
        }

        var year = DateTime.UtcNow.Year;
        var accounts = await LoadDemoAccountsAsync(db, fiscalYearId);
        var supplierInvoiceService = new SupplierInvoiceService(db);

        // Left unposted so the "obokförd leverantörsfaktura" demo state exists.
        var (unposted, unpostedError) = await supplierInvoiceService.CreateAsync(new SupplierInvoice
        {
            FiscalYearId = fiscalYearId,
            SupplierName = "Kontorsmaterial Nord AB",
            InvoiceNumber = "F-2024-118",
            InvoiceDate = new DateOnly(year, 7, 2),
            DueDate = new DateOnly(year, 8, 1),
            AmountExclVat = 1200m,
            VatAmount = 300m,
            TotalAmount = 1500m
        });
        if (unpostedError is not null)
            throw new InvalidOperationException($"Demo seed failed to create supplier invoice: {unpostedError}");

        var (paidInvoice, paidCreateError) = await supplierInvoiceService.CreateAsync(new SupplierInvoice
        {
            FiscalYearId = fiscalYearId,
            SupplierName = "Städservice Karlsson AB",
            InvoiceNumber = "2024-0087",
            InvoiceDate = new DateOnly(year, 7, 8),
            DueDate = new DateOnly(year, 8, 7),
            AmountExclVat = 4000m,
            VatAmount = 1000m,
            TotalAmount = 5000m
        });
        if (paidCreateError is not null)
            throw new InvalidOperationException($"Demo seed failed to create supplier invoice: {paidCreateError}");

        // PostAsync/MarkAsPaidAsync open their own transactions; wrap in the execution strategy
        // since EnrichNpgsqlDbContext's retrying strategy refuses transactions run outside of it.
        var strategy = db.Database.CreateExecutionStrategy();
        var (_, postError) = await strategy.ExecuteAsync(() => supplierInvoiceService.PostAsync(
            paidInvoice!.Id, accounts["5010"].Id, accounts["2440"].Id, accounts["2641"].Id));
        if (postError is not null)
            throw new InvalidOperationException($"Demo seed failed to post supplier invoice: {postError}");

        var (_, payError) = await strategy.ExecuteAsync(() => supplierInvoiceService.MarkAsPaidAsync(
            paidInvoice.Id, new DateOnly(year, 7, 13), accounts["1910"].Id, accounts["2440"].Id));
        if (payError is not null)
            throw new InvalidOperationException($"Demo seed failed to mark supplier invoice as paid: {payError}");

        // A draft (unposted) customer invoice so the customer-invoice flow is testable end to end.
        var customerInvoiceService = new CustomerInvoiceService(db);
        var draftInvoice = new CustomerInvoice
        {
            FiscalYearId = fiscalYearId,
            CustomerId = nordicDesign!.Id,
            CustomerName = nordicDesign.Name,
            InvoiceDate = new DateOnly(year, 7, 14),
            DueDate = new DateOnly(year, 8, 13),
            OurReference = "Admin"
        };
        List<CustomerInvoiceLine> draftLines =
        [
            new() { Description = "Konsulttimmar – webbutveckling", Quantity = 20, UnitPrice = 950m, VatRate = 25 },
            new() { Description = "Domän & hosting", Quantity = 1, UnitPrice = 800m, VatRate = 25 }
        ];
        var (_, draftError) = await customerInvoiceService.CreateAsync(draftInvoice, draftLines);
        if (draftError is not null)
            throw new InvalidOperationException($"Demo seed failed to create draft customer invoice: {draftError}");
    }
}
