using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

/// <summary>
/// P0 #5: Reversal closed-year check tests.
/// Current bug: CreateReversalAsync does not check if the fiscal year is closed,
/// allowing reversals that violate accounting period integrity.
/// </summary>
public class ReversalClosedYearTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _account1;
    private readonly Account _account2;

    public ReversalClosedYearTests()
    {
        _f = new TestFixture();
        _fiscalYear = _f.CreateFiscalYear();
        _account1 = _f.CreateAccount(_fiscalYear.Id, "1910", "Kassa");
        _account2 = _f.CreateAccount(_fiscalYear.Id, "3010", "Försäljning", AccountClass.Revenue);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task ReverseEntry_InClosedFiscalYear_ReturnsError()
    {
        // Create and post an entry while year is still open
        var (entry, createError) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000m));
        Assert.Null(createError);
        Assert.NotNull(entry);
        var postError = await _f.JournalEntryService.PostAsync(entry.Id);
        Assert.Null(postError);

        // Close the fiscal year
        _fiscalYear.IsClosed = true;
        await _f.Db.SaveChangesAsync();

        // Attempt reversal — should fail
        var (reversal, error) = await _f.JournalEntryService.CreateReversalAsync(entry.Id, "Correction");

        Assert.Null(reversal);
        Assert.NotNull(error);
        Assert.Contains("closed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReverseEntry_InOpenFiscalYear_Succeeds()
    {
        // Create and post an entry
        var (entry, createError) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000m));
        Assert.Null(createError);
        Assert.NotNull(entry);
        var postError = await _f.JournalEntryService.PostAsync(entry.Id);
        Assert.Null(postError);

        // Year is still open — reversal should succeed
        var (reversal, error) = await _f.JournalEntryService.CreateReversalAsync(entry.Id, "Correction");

        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.True(reversal.IsPosted);
        Assert.Contains("Reversal", reversal.Description);
    }

    [Fact]
    public async Task ReverseEntry_InClosedYear_DoesNotCreateReversal()
    {
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(500m));
        await _f.JournalEntryService.PostAsync(entry!.Id);

        _fiscalYear.IsClosed = true;
        await _f.Db.SaveChangesAsync();

        await _f.JournalEntryService.CreateReversalAsync(entry.Id, "Oops");

        // Verify no reversal entry was created
        var entries = await _f.Db.JournalEntries.ToListAsync();
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
