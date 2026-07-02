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

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(DemoUserEmail) is not null)
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

        var currentYearNumber = DateTime.UtcNow.Year;

        var previousFiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = (currentYearNumber - 1).ToString(),
            StartDate = new DateOnly(currentYearNumber - 1, 1, 1),
            EndDate = new DateOnly(currentYearNumber - 1, 12, 31),
            IsClosed = false
        };
        var currentFiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = currentYearNumber.ToString(),
            StartDate = new DateOnly(currentYearNumber, 1, 1),
            EndDate = new DateOnly(currentYearNumber, 12, 31),
            IsClosed = false
        };
        db.FiscalYears.AddRange(previousFiscalYear, currentFiscalYear);
        await db.SaveChangesAsync();

        await new BasImportService(db).ImportDefaultAsync(previousFiscalYear.Id);
        await new BasImportService(db).ImportDefaultAsync(currentFiscalYear.Id);

        await SeedPreviousYearEntriesAsync(db, previousFiscalYear);
        await SeedCurrentYearEntriesAsync(db, currentFiscalYear.Id);
    }

    private static async Task<Dictionary<string, Account>> LoadDemoAccountsAsync(AppDbContext db, int fiscalYearId)
    {
        return await db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Where(a => a.AccountNumber == "1910" || a.AccountNumber == "2440"
                || a.AccountNumber == "2081" || a.AccountNumber == "3001" || a.AccountNumber == "5010")
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

        await journalEntryService.PostAsync(created!.Id);
    }

    private static async Task SeedPreviousYearEntriesAsync(AppDbContext db, FiscalYear previousFiscalYear)
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

        previousFiscalYear.IsClosed = true;
        previousFiscalYear.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
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

        // Leave voucher #3 as a gap: bypass JournalEntryService (which blocks deleting posted
        // entries) to simulate a historical direct-DB deletion — the exact scenario BFNAR 2013:2
        // gap detection exists to catch. Entry numbers are assigned sequentially by
        // JournalEntryService.CreateAsync in the order entries were posted above, so re-querying
        // ordered by EntryNumber and taking the 3rd one identifies the entry to delete.
        var postedEntryIds = await db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .OrderBy(j => j.EntryNumber)
            .Select(j => j.Id)
            .ToListAsync();

        var gapEntry = await db.JournalEntries
            .Include(j => j.Lines)
            .FirstAsync(j => j.Id == postedEntryIds[2]);
        db.JournalEntryLines.RemoveRange(gapEntry.Lines);
        db.JournalEntries.Remove(gapEntry);
        await db.SaveChangesAsync();
    }
}
