using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class AuditTrailTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly JournalEntryService _service;
    private readonly FiscalYear _fiscalYear;
    private readonly Account _account1;
    private readonly Account _account2;

    public AuditTrailTests()
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

        _account1 = new Account { AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset, FiscalYearId = _fiscalYear.Id };
        _account2 = new Account { AccountNumber = "3010", Name = "Försäljning", AccountClass = AccountClass.Revenue, FiscalYearId = _fiscalYear.Id };
        _db.Accounts.AddRange(_account1, _account2);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

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
        var (entry, _) = await _service.CreateAsync(MakeEntry(1000));
        Assert.False(entry!.IsPosted);

        var error = await _service.PostAsync(entry.Id);

        Assert.Null(error);
        var reloaded = await _db.JournalEntries.FindAsync(entry.Id);
        Assert.True(reloaded!.IsPosted);
    }

    [Fact]
    public async Task UpdatePostedEntry_ReturnsError()
    {
        var (entry, _) = await _service.CreateAsync(MakeEntry(1000));
        await _service.PostAsync(entry!.Id);

        var updated = MakeEntry(2000);
        updated.Id = entry.Id;
        var (_, error) = await _service.UpdateAsync(updated);

        Assert.NotNull(error);
        Assert.Contains("posted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateDraftEntry_Succeeds()
    {
        var (entry, _) = await _service.CreateAsync(MakeEntry(1000));

        var updated = MakeEntry(2000);
        updated.Id = entry!.Id;
        var (result, error) = await _service.UpdateAsync(updated);

        Assert.Null(error);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateReversal_SwapsDebitsAndCredits()
    {
        var (entry, _) = await _service.CreateAsync(MakeEntry(1000));
        await _service.PostAsync(entry!.Id);

        var (reversal, error) = await _service.CreateReversalAsync(entry.Id, "correction");

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
        var (entry, _) = await _service.CreateAsync(MakeEntry(1000));

        var (_, error) = await _service.CreateReversalAsync(entry!.Id, "test");

        Assert.NotNull(error);
        Assert.Contains("posted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateReversal_IsAutomaticallyPosted()
    {
        var (entry, _) = await _service.CreateAsync(MakeEntry(1000));
        await _service.PostAsync(entry!.Id);

        var (reversal, _) = await _service.CreateReversalAsync(entry.Id, "auto-post test");

        Assert.NotNull(reversal);
        Assert.True(reversal.IsPosted);
    }
}
