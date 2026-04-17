using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class AuditTrailTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _account1;
    private readonly Account _account2;

    public AuditTrailTests()
    {
        _f = new TestFixture();
        _fiscalYear = _f.CreateFiscalYear();
        _account1 = _f.CreateAccount(_fiscalYear.Id, "1910", "Kassa");
        _account2 = _f.CreateAccount(_fiscalYear.Id, "3010", "Försäljning", AccountClass.Revenue);
    }

    public void Dispose() => _f.Dispose();

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

    [Fact]
    public async Task PostEntry_SetsIsPosted()
    {
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));
        Assert.False(entry!.IsPosted);

        var error = await _f.JournalEntryService.PostAsync(entry.Id);

        Assert.Null(error);
        var reloaded = await _f.Db.JournalEntries.FindAsync(entry.Id);
        Assert.True(reloaded!.IsPosted);
    }

    [Fact]
    public async Task UpdatePostedEntry_ReturnsError()
    {
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));
        await _f.JournalEntryService.PostAsync(entry!.Id);

        var updated = MakeEntry(2000);
        updated.Id = entry.Id;
        var (_, error) = await _f.JournalEntryService.UpdateAsync(updated);

        Assert.NotNull(error);
        Assert.Contains("posted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateDraftEntry_Succeeds()
    {
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));

        var updated = MakeEntry(2000);
        updated.Id = entry!.Id;
        var (result, error) = await _f.JournalEntryService.UpdateAsync(updated);

        Assert.Null(error);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateReversal_SwapsDebitsAndCredits()
    {
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));
        await _f.JournalEntryService.PostAsync(entry!.Id);

        var (reversal, error) = await _f.JournalEntryService.CreateReversalAsync(entry.Id, "correction");

        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.Contains("Reversal of #", reversal.Description);

        var reversalLines = reversal.Lines.OrderBy(l => l.AccountId).ToList();
        var originalLines = entry.Lines.OrderBy(l => l.AccountId).ToList();

        for (int i = 0; i < originalLines.Count; i++)
        {
            Assert.Equal(originalLines[i].DebitAmount, reversalLines[i].CreditAmount);
            Assert.Equal(originalLines[i].CreditAmount, reversalLines[i].DebitAmount);
        }
    }

    [Fact]
    public async Task CreateReversal_OnlyWorksOnPostedEntries()
    {
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));

        var (_, error) = await _f.JournalEntryService.CreateReversalAsync(entry!.Id, "test");

        Assert.NotNull(error);
        Assert.Contains("posted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateReversal_IsAutomaticallyPosted()
    {
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));
        await _f.JournalEntryService.PostAsync(entry!.Id);

        var (reversal, _) = await _f.JournalEntryService.CreateReversalAsync(entry.Id, "auto-post test");

        Assert.NotNull(reversal);
        Assert.True(reversal.IsPosted);
    }
}
