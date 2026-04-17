using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #5: Reversal closed-year check tests.
/// Current bug: CreateReversalAsync does not check if the fiscal year is closed,
/// allowing reversals that violate accounting period integrity.
/// </summary>
public class ReversalClosedYearTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _account1;
    private readonly Account _account2;

    public ReversalClosedYearTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _service = new JournalEntryService(_db);

        _fiscalYear = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(_fiscalYear);
        _db.SaveChanges();

        _account1 = new Account
        {
            AccountNumber = "1910",
            Name = "Kassa",
            AccountClass = AccountClass.Asset,
            FiscalYearId = _fiscalYear.Id
        };
        _account2 = new Account
        {
            AccountNumber = "3010",
            Name = "Försäljning",
            AccountClass = AccountClass.Revenue,
            FiscalYearId = _fiscalYear.Id
        };
        _db.Accounts.AddRange(_account1, _account2);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ReverseEntry_InClosedFiscalYear_ReturnsError()
    {
        // Create and post an entry while year is still open
        var (entry, createError) = await _service.CreateAsync(MakeEntry(1000m));
        Assert.Null(createError);
        Assert.NotNull(entry);
        var postError = await _service.PostAsync(entry.Id);
        Assert.Null(postError);

        // Close the fiscal year
        _fiscalYear.IsClosed = true;
        await _db.SaveChangesAsync();

        // Attempt reversal — should fail
        var (reversal, error) = await _service.CreateReversalAsync(entry.Id, "Correction");

        Assert.Null(reversal);
        Assert.NotNull(error);
        Assert.Contains("closed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseEntry_InOpenFiscalYear_Succeeds()
    {
        // Create and post an entry
        var (entry, createError) = await _service.CreateAsync(MakeEntry(1000m));
        Assert.Null(createError);
        Assert.NotNull(entry);
        var postError = await _service.PostAsync(entry.Id);
        Assert.Null(postError);

        // Year is still open — reversal should succeed
        var (reversal, error) = await _service.CreateReversalAsync(entry.Id, "Correction");

        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.True(reversal.IsPosted);
        Assert.Contains("Reversal", reversal.Description);
    }

    [Fact]
    public async Task ReverseEntry_InClosedYear_DoesNotCreateReversal()
    {
        var (entry, _) = await _service.CreateAsync(MakeEntry(500m));
        await _service.PostAsync(entry!.Id);

        _fiscalYear.IsClosed = true;
        await _db.SaveChangesAsync();

        await _service.CreateReversalAsync(entry.Id, "Oops");

        // Verify no reversal entry was created
        var entries = await _db.JournalEntries.ToListAsync();
        Assert.Single(entries); // Only the original entry
    }

    private JournalEntry MakeEntry(decimal amount) => new()
    {
        Date = new DateOnly(2026, 3, 1),
        Description = $"Test entry {amount}",
        FiscalYearId = _fiscalYear.Id,
        Lines =
        [
            new() { AccountId = _account1.Id, DebitAmount = amount, CreditAmount = 0 },
            new() { AccountId = _account2.Id, DebitAmount = 0, CreditAmount = amount }
        ]
    };
}
