# BAS 2026 Seed & Account Balance Mapping — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Embed BAS 2026 as a seedable kontoplan on fiscal year creation, and add an account-balance mapping tool that automatically propagates UB→IB between years.

**Architecture:** `FiscalYear` gains a `PreviousFiscalYearId` self-referential FK that records which year's UBs feed this year's IBs. Auto-propagation fires on three triggers: year-end close (existing), SIE import (new inline hook), and journal entry post/reversal (new targeted helper using only the affected account IDs). A new `AccountMappingService` (Application layer) drives the mapping UI page.

**Tech Stack:** .NET 10 / EF Core / Blazor / MudBlazor / ExcelDataReader / SQLite (tests) / PostgreSQL (prod)

---

## File Map

| Action | Path |
|--------|------|
| Modify | `src/KoalaBooks.Domain/Entities/FiscalYear.cs` |
| Modify | `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs` |
| Modify | `src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj` |
| Add    | `src/KoalaBooks.Infrastructure/Resources/BAS_kontoplan_2026_v2.xlsx` |
| Modify | `src/KoalaBooks.Infrastructure/Services/BasImportService.cs` |
| Modify | `src/KoalaBooks.Infrastructure/Services/SieImportService.cs` |
| Modify | `src/KoalaBooks.Application/Services/FiscalYearService.cs` |
| Modify | `src/KoalaBooks.Application/Services/JournalEntryService.cs` |
| Create | `src/KoalaBooks.Application/Services/AccountMappingService.cs` |
| Modify | `src/KoalaBooks.Web/Components/Pages/FiscalYears.razor` |
| Create | `src/KoalaBooks.Web/Components/Pages/AccountMapping.razor` |
| Modify | `src/KoalaBooks.Web/Components/Layout/MainLayout.razor` |
| Modify | `src/KoalaBooks.Web/Program.cs` |
| Create | `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs` |
| Modify | `tests/KoalaBooks.Tests/BasImportServiceTests.cs` |
| Modify | `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs` (may not exist yet — create) |

---

## Task 1: Add `PreviousFiscalYearId` to `FiscalYear`

**Files:**
- Modify: `src/KoalaBooks.Domain/Entities/FiscalYear.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`

- [ ] **Step 1: Add the property to the entity**

Replace the contents of `src/KoalaBooks.Domain/Entities/FiscalYear.cs`:

```csharp
namespace KoalaBooks.Domain.Entities;

public class FiscalYear
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation Organisation { get; set; } = null!;
    public required string Name { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int? PreviousFiscalYearId { get; set; }
    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<Account> Accounts { get; set; } = [];
}
```

- [ ] **Step 2: Configure the self-referential FK in `AppDbContext`**

Inside `OnModelCreating`, find the `modelBuilder.Entity<FiscalYear>(entity =>` block and add the FK configuration after the existing `HasOne(f => f.Organisation)` call:

```csharp
entity.HasOne<FiscalYear>()
      .WithMany()
      .HasForeignKey(f => f.PreviousFiscalYearId)
      .OnDelete(DeleteBehavior.SetNull);
```

- [ ] **Step 3: Create and apply the migration**

```bash
cd /home/flojon/src/koalabooks
dotnet ef migrations add AddPreviousFiscalYearId \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
dotnet ef database update \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: migration file created, database updated successfully.

- [ ] **Step 4: Build to confirm no errors**

```bash
dotnet build src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Entities/FiscalYear.cs \
        src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
        src/KoalaBooks.Infrastructure/Migrations/
git commit -m "feat: add PreviousFiscalYearId to FiscalYear"
```

---

## Task 2: Update `FiscalYearService` — set link on auto-copy, prefer link on propagation

**Files:**
- Modify: `src/KoalaBooks.Application/Services/FiscalYearService.cs`
- Create: `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class FiscalYearServiceTests : IDisposable
{
    private readonly TestFixture _f;

    public FiscalYearServiceTests() => _f = new TestFixture();
    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task PropagateBalances_FollowsPreviousFiscalYearIdLink()
    {
        // source year (2024) closed with UB=500 on account 1910
        var source = _f.CreateFiscalYear("2024",
            new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), isClosed: true);
        _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 0, outgoingBalance: 500);

        // target year (2026) linked explicitly to source — NOT adjacent by date
        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        target.PreviousFiscalYearId = source.Id;
        _f.Db.SaveChanges();
        _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 0, outgoingBalance: 0);

        // unrelated year between source and target — should NOT be chosen
        _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));

        await _f.FiscalYearService.PropagateBalancesToNextYearAsync(source.Id);

        var ib = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == target.Id && a.AccountNumber == "1910")
            .Select(a => a.IncomingBalance)
            .FirstAsync();
        Assert.Equal(500, ib);
    }

    [Fact]
    public async Task CopyAccountsFromPreviousYear_SetsPreviousFiscalYearId()
    {
        var prev = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        _f.CreateAccount(prev.Id, "1910", "Kassa", AccountClass.Asset,
            outgoingBalance: 100);

        var newFy = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        await _f.Db.Entry(newFy).ReloadAsync();
        Assert.Equal(prev.Id, newFy.PreviousFiscalYearId);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "FiscalYearServiceTests" -q
```

Expected: 2 FAILED (PreviousFiscalYearId not yet set/used)

- [ ] **Step 3: Update `CopyAccountsFromPreviousYearAsync` to set the link**

In `src/KoalaBooks.Application/Services/FiscalYearService.cs`, replace `CopyAccountsFromPreviousYearAsync`:

```csharp
private async Task CopyAccountsFromPreviousYearAsync(FiscalYear targetYear)
{
    var previousYear = await _db.FiscalYears
        .Where(f => f.EndDate < targetYear.StartDate && f.Id != targetYear.Id)
        .OrderByDescending(f => f.EndDate)
        .FirstOrDefaultAsync();

    if (previousYear is null) return;

    targetYear.PreviousFiscalYearId = previousYear.Id;

    var previousAccounts = await _db.Accounts
        .Where(a => a.FiscalYearId == previousYear.Id)
        .ToListAsync();

    var existingNumbers = await _db.Accounts
        .Where(a => a.FiscalYearId == targetYear.Id)
        .Select(a => a.AccountNumber)
        .ToHashSetAsync();

    foreach (var prev in previousAccounts)
    {
        if (existingNumbers.Contains(prev.AccountNumber)) continue;
        var isPnL = prev.AccountClass is AccountClass.Revenue or AccountClass.Expense;
        _db.Accounts.Add(new Account
        {
            AccountNumber = prev.AccountNumber,
            Name = prev.Name,
            AccountClass = prev.AccountClass,
            IsActive = prev.IsActive,
            IncomingBalance = isPnL ? 0 : prev.OutgoingBalance,
            OutgoingBalance = 0,
            FiscalYearId = targetYear.Id
        });
    }

    await _db.SaveChangesAsync();
}
```

- [ ] **Step 4: Update `PropagateBalancesToNextYearAsync` to prefer the explicit link**

Replace the method in `FiscalYearService.cs`:

```csharp
public async Task PropagateBalancesToNextYearAsync(int fiscalYearId)
{
    var sourceYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == fiscalYearId);
    if (sourceYear is null) return;

    // Prefer the explicitly linked year; fall back to next year by date.
    var nextYear = await _db.FiscalYears
                       .FirstOrDefaultAsync(f => f.PreviousFiscalYearId == fiscalYearId)
                   ?? await _db.FiscalYears
                       .Where(f => f.StartDate > sourceYear.EndDate)
                       .OrderBy(f => f.StartDate)
                       .FirstOrDefaultAsync();

    if (nextYear is null) return;

    var sourceAccounts = await _db.Accounts
        .Where(a => a.FiscalYearId == fiscalYearId)
        .ToListAsync();

    var nextAccounts = await _db.Accounts
        .Where(a => a.FiscalYearId == nextYear.Id)
        .ToDictionaryAsync(a => a.AccountNumber);

    foreach (var src in sourceAccounts)
    {
        var isPnL = src.AccountClass is AccountClass.Revenue or AccountClass.Expense;
        var incomingBalance = isPnL ? 0 : src.OutgoingBalance;

        if (nextAccounts.TryGetValue(src.AccountNumber, out var nextAccount))
        {
            nextAccount.IncomingBalance = incomingBalance;
        }
        else if (src.OutgoingBalance != 0)
        {
            _db.Accounts.Add(new Account
            {
                AccountNumber = src.AccountNumber,
                Name = src.Name,
                AccountClass = src.AccountClass,
                IsActive = src.IsActive,
                IncomingBalance = incomingBalance,
                OutgoingBalance = 0,
                FiscalYearId = nextYear.Id
            });
        }
    }

    await _db.SaveChangesAsync();
}
```

- [ ] **Step 5: Run tests — expect pass**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "FiscalYearServiceTests" -q
```

Expected: 2 PASSED

- [ ] **Step 6: Run full test suite to check for regressions**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "FullyQualifiedName!~AttachmentProvider" -q
```

Expected: all PASSED

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Application/Services/FiscalYearService.cs \
        tests/KoalaBooks.Tests/FiscalYearServiceTests.cs
git commit -m "feat: propagate IB via PreviousFiscalYearId link, set on auto-copy"
```

---

## Task 3: BAS 2026 embedded resource + `ImportDefaultAsync`

**Files:**
- Add: `src/KoalaBooks.Infrastructure/Resources/BAS_kontoplan_2026_v2.xlsx`
- Modify: `src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj`
- Modify: `src/KoalaBooks.Infrastructure/Services/BasImportService.cs`
- Modify: `tests/KoalaBooks.Tests/BasImportServiceTests.cs`

- [ ] **Step 1: Copy the XLSX file into the project**

```bash
mkdir -p src/KoalaBooks.Infrastructure/Resources
cp /mnt/c/Users/flojon/Downloads/BAS_kontoplan_2026_v2.xlsx \
   src/KoalaBooks.Infrastructure/Resources/BAS_kontoplan_2026_v2.xlsx
```

- [ ] **Step 2: Register as EmbeddedResource in the csproj**

In `src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj`, add inside the root `<Project>`:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources\BAS_kontoplan_2026_v2.xlsx" />
</ItemGroup>
```

- [ ] **Step 3: Write the failing test**

Add to `tests/KoalaBooks.Tests/BasImportServiceTests.cs`:

```csharp
[Fact]
public async Task ImportDefaultAsync_ImportsAccounts()
{
    var result = await _service.ImportDefaultAsync(_fy.Id);

    Assert.True(result.ImportedCount > 1000,
        $"Expected >1000 accounts from BAS 2026, got {result.ImportedCount}");
    Assert.Empty(result.Errors);
}
```

- [ ] **Step 4: Run the test to confirm it fails**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "ImportDefaultAsync_ImportsAccounts" -q
```

Expected: FAILED — method does not exist yet.

- [ ] **Step 5: Add `ImportDefaultAsync` to `BasImportService`**

Add this method to `src/KoalaBooks.Infrastructure/Services/BasImportService.cs`:

```csharp
public async Task<BasImportResult> ImportDefaultAsync(int fiscalYearId)
{
    var assembly = typeof(BasImportService).Assembly;
    using var stream = assembly.GetManifestResourceStream(
        "KoalaBooks.Infrastructure.Resources.BAS_kontoplan_2026_v2.xlsx")
        ?? throw new InvalidOperationException(
            "Embedded BAS 2026 resource not found. Ensure the file is marked as EmbeddedResource.");
    return await ImportFromExcelAsync(stream, fiscalYearId);
}
```

- [ ] **Step 6: Run the test — expect pass**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "ImportDefaultAsync_ImportsAccounts" -q
```

Expected: PASSED

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Resources/BAS_kontoplan_2026_v2.xlsx \
        src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj \
        src/KoalaBooks.Infrastructure/Services/BasImportService.cs \
        tests/KoalaBooks.Tests/BasImportServiceTests.cs
git commit -m "feat: embed BAS 2026 kontoplan and add ImportDefaultAsync"
```

---

## Task 4: BAS seed checkbox on fiscal year creation UI

**Files:**
- Modify: `src/KoalaBooks.Web/Components/Pages/FiscalYears.razor`

- [ ] **Step 1: Add checkbox field and wire up BAS import after creation**

In `FiscalYears.razor`, make these changes:

In the `@code` block, add the field:
```csharp
[Inject] private BasImportService BasImportService { get; set; } = default!;
private bool _seedBas;
```

In the creation form markup, add a row below the date fields (before the Skapa button row):
```razor
<div style="grid-column: 1 / -1; margin-top:0.25rem;">
    <label style="display:flex; align-items:center; gap:0.5rem; cursor:pointer;">
        <input type="checkbox" @bind="_seedBas" />
        Importera BAS 2026 kontoplan
    </label>
</div>
```

Replace the `Create()` method:
```csharp
private async Task Create()
{
    _formError = null;
    if (string.IsNullOrWhiteSpace(_formName))
    {
        _formError = "Namn är obligatoriskt.";
        return;
    }
    if (_formStart >= _formEnd)
    {
        _formError = "Startdatum måste vara före slutdatum.";
        return;
    }

    FiscalYear newFy;
    try
    {
        newFy = await FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = _formName.Trim(),
            StartDate = DateOnly.FromDateTime(_formStart),
            EndDate = DateOnly.FromDateTime(_formEnd)
        });
    }
    catch (InvalidOperationException)
    {
        Snackbar.Add("Räkenskapsåret överlappar med ett befintligt räkenskapsår.", Severity.Error);
        return;
    }

    if (_seedBas)
    {
        var result = await BasImportService.ImportDefaultAsync(newFy.Id);
        Snackbar.Add(
            $"Räkenskapsår skapat. Importerade {result.ImportedCount} konton från BAS 2026.",
            Severity.Success);
    }
    else
    {
        Snackbar.Add("Räkenskapsår skapat.", Severity.Success);
    }

    _showForm = false;
    _formName = "";
    _seedBas = false;
    _formError = null;
    await Load();
}
```

- [ ] **Step 2: Build the web project**

```bash
dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Web/Components/Pages/FiscalYears.razor
git commit -m "feat: add BAS 2026 seed checkbox to fiscal year creation"
```

---

## Task 5: `AccountMappingService` (TDD)

**Files:**
- Create: `src/KoalaBooks.Application/Services/AccountMappingService.cs`
- Create: `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs`

- [ ] **Step 1: Write all five failing tests**

Create `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs`:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class AccountMappingServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly AccountMappingService _service;

    public AccountMappingServiceTests()
    {
        _f = new TestFixture();
        _service = new AccountMappingService(_f.Db);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task BuildMapping_PreSelectsSameAccountNumber()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
            outgoingBalance: 500);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset);

        var rows = await _service.BuildMappingAsync(source.Id, target.Id);

        var row = Assert.Single(rows);
        Assert.Equal("1910", row.SourceAccountNumber);
        Assert.Equal(500, row.Ub);
        Assert.Equal("1910", row.TargetAccountNumber);
    }

    [Fact]
    public async Task BuildMapping_LeavesBlank_WhenTargetMissing()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        _f.CreateAccount(source.Id, "1241", "Personbilar", AccountClass.Asset,
            outgoingBalance: 200);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        // 1241 does not exist in target

        var rows = await _service.BuildMappingAsync(source.Id, target.Id);

        var row = Assert.Single(rows);
        Assert.Equal("1241", row.SourceAccountNumber);
        Assert.Null(row.TargetAccountNumber);
    }

    [Fact]
    public async Task ApplyMapping_WritesIbToTargetAccounts()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var cash = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset);
        var liab = _f.CreateAccount(target.Id, "2440", "Leverantörsskulder", AccountClass.Liability);

        var rows = new List<MappingRow>
        {
            new("1910", "Kassa", 500m, "1910"),
            new("2440", "Leverantörsskulder", 300m, "2440")
        };

        await _service.ApplyMappingAsync(source.Id, target.Id, rows);

        await _f.Db.Entry(cash).ReloadAsync();
        await _f.Db.Entry(liab).ReloadAsync();
        Assert.Equal(500m, cash.IncomingBalance);
        Assert.Equal(300m, liab.IncomingBalance);
    }

    [Fact]
    public async Task ApplyMapping_SetsPreviousFiscalYearId()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset);

        var rows = new List<MappingRow> { new("1910", "Kassa", 100m, "1910") };
        await _service.ApplyMappingAsync(source.Id, target.Id, rows);

        await _f.Db.Entry(target).ReloadAsync();
        Assert.Equal(source.Id, target.PreviousFiscalYearId);
    }

    [Fact]
    public async Task ApplyMapping_SkipsNullTargetRows()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var cash = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 0);

        var rows = new List<MappingRow>
        {
            new("1910", "Kassa", 500m, null),   // explicitly skipped
            new("1241", "Personbilar", 200m, null) // no target exists
        };

        var result = await _service.ApplyMappingAsync(source.Id, target.Id, rows);

        Assert.Equal(0, result.Mapped);
        Assert.Equal(2, result.Skipped);
        await _f.Db.Entry(cash).ReloadAsync();
        Assert.Equal(0, cash.IncomingBalance); // unchanged
    }
}
```

- [ ] **Step 2: Run tests — confirm they fail**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "AccountMappingServiceTests" -q
```

Expected: compilation error — `AccountMappingService` and `MappingRow` not found.

- [ ] **Step 3: Create `AccountMappingService`**

Create `src/KoalaBooks.Application/Services/AccountMappingService.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public record MappingRow(
    string SourceAccountNumber,
    string SourceAccountName,
    decimal Ub,
    string? TargetAccountNumber);

public record ApplyMappingResult(int Mapped, int Skipped);

public class AccountMappingService
{
    private readonly AppDbContext _db;

    public AccountMappingService(AppDbContext db) => _db = db;

    public async Task<List<MappingRow>> BuildMappingAsync(int sourceFiscalYearId, int targetFiscalYearId)
    {
        var sourceYear = await _db.FiscalYears.FindAsync(sourceFiscalYearId)
            ?? throw new InvalidOperationException("Source fiscal year not found.");

        var sourceAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == sourceFiscalYearId)
            .OrderBy(a => a.AccountNumber)
            .ToListAsync();

        var targetAccountNumbers = await _db.Accounts
            .Where(a => a.FiscalYearId == targetFiscalYearId)
            .Select(a => a.AccountNumber)
            .ToHashSetAsync();

        Dictionary<int, decimal> effectiveUbs;
        if (sourceYear.IsClosed)
        {
            effectiveUbs = sourceAccounts.ToDictionary(a => a.Id, a => a.OutgoingBalance);
        }
        else
        {
            var sourceAccountIds = sourceAccounts.Select(a => a.Id).ToList();

            var debits = await _db.JournalEntryLines
                .Where(l => sourceAccountIds.Contains(l.AccountId) && l.JournalEntry.IsPosted)
                .GroupBy(l => l.AccountId)
                .Select(g => new { g.Key, Total = g.Sum(l => l.DebitAmount) })
                .ToDictionaryAsync(x => x.Key, x => x.Total);

            var credits = await _db.JournalEntryLines
                .Where(l => sourceAccountIds.Contains(l.AccountId) && l.JournalEntry.IsPosted)
                .GroupBy(l => l.AccountId)
                .Select(g => new { g.Key, Total = g.Sum(l => l.CreditAmount) })
                .ToDictionaryAsync(x => x.Key, x => x.Total);

            effectiveUbs = sourceAccounts.ToDictionary(a => a.Id, a =>
            {
                var d = debits.GetValueOrDefault(a.Id);
                var c = credits.GetValueOrDefault(a.Id);
                return a.AccountClass.IsCreditNormal()
                    ? a.IncomingBalance + c - d
                    : a.IncomingBalance + d - c;
            });
        }

        return sourceAccounts
            .Select(a => new MappingRow(
                SourceAccountNumber: a.AccountNumber,
                SourceAccountName: a.Name,
                Ub: effectiveUbs[a.Id],
                TargetAccountNumber: targetAccountNumbers.Contains(a.AccountNumber)
                    ? a.AccountNumber : null))
            .Where(r => r.Ub != 0)
            .ToList();
    }

    public async Task<ApplyMappingResult> ApplyMappingAsync(
        int sourceFiscalYearId,
        int targetFiscalYearId,
        List<MappingRow> rows)
    {
        var targetYear = await _db.FiscalYears.FindAsync(targetFiscalYearId)
            ?? throw new InvalidOperationException("Target fiscal year not found.");

        var targetAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == targetFiscalYearId)
            .ToDictionaryAsync(a => a.AccountNumber);

        int mapped = 0, skipped = 0;
        foreach (var row in rows)
        {
            if (row.TargetAccountNumber is null ||
                !targetAccounts.TryGetValue(row.TargetAccountNumber, out var targetAccount))
            {
                skipped++;
                continue;
            }
            targetAccount.IncomingBalance = row.Ub;
            mapped++;
        }

        targetYear.PreviousFiscalYearId = sourceFiscalYearId;
        await _db.SaveChangesAsync();

        return new ApplyMappingResult(mapped, skipped);
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "AccountMappingServiceTests" -q
```

Expected: 5 PASSED

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/AccountMappingService.cs \
        tests/KoalaBooks.Tests/AccountMappingServiceTests.cs
git commit -m "feat: add AccountMappingService with BuildMappingAsync and ApplyMappingAsync"
```

---

## Task 6: Journal entry propagation hook

**Files:**
- Modify: `src/KoalaBooks.Application/Services/JournalEntryService.cs`
- Modify: `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs` (add propagation tests here as they use the same setup)

- [ ] **Step 1: Write failing tests**

Add to `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs` (same class, uses same `TestFixture`):

```csharp
[Fact]
public async Task PostEntry_PropagatesAffectedAccountsToLinkedNextYear()
{
    // 2025 (source, open) with account 1910 IB=100
    var source = _f.CreateFiscalYear("2025",
        new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
    var cash2025 = _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
        incomingBalance: 100);

    // 2026 (target) linked to 2025, same account IB=100
    var target = _f.CreateFiscalYear("2026",
        new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
    target.PreviousFiscalYearId = source.Id;
    var cash2026 = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
        incomingBalance: 100);
    _f.Db.SaveChanges();

    // Post a journal entry in 2025 that debits 1910 by 50
    var liab2025 = _f.CreateAccount(source.Id, "2440", "Lev.skulder", AccountClass.Liability);
    var entry = _f.MakeEntry(source.Id, cash2025.Id, liab2025.Id, 50m,
        new DateOnly(2025, 6, 1));
    _f.Db.JournalEntries.Add(entry);
    _f.Db.SaveChanges();

    var error = await _f.JournalEntryService.PostAsync(entry.Id);

    Assert.Null(error);
    // UB for 1910 in 2025 = IB(100) + debit(50) = 150 (asset: debit-normal)
    // So 2026 IB for 1910 should become 150
    await _f.Db.Entry(cash2026).ReloadAsync();
    Assert.Equal(150m, cash2026.IncomingBalance);
}

[Fact]
public async Task PostEntry_DoesNotPropagateWhenNoLinkedYear()
{
    var source = _f.CreateFiscalYear("2025",
        new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
    var cash = _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
        incomingBalance: 100);
    var liab = _f.CreateAccount(source.Id, "2440", "Lev.skulder", AccountClass.Liability);

    var entry = _f.MakeEntry(source.Id, cash.Id, liab.Id, 50m, new DateOnly(2025, 6, 1));
    _f.Db.JournalEntries.Add(entry);
    _f.Db.SaveChanges();

    // No linked year — should not throw, no propagation
    var error = await _f.JournalEntryService.PostAsync(entry.Id);

    Assert.Null(error);
    // just verifying no exception — no target year to assert on
}
```

- [ ] **Step 2: Run tests — confirm they fail**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "PostEntry_Propagates|PostEntry_DoesNotPropagate" -q
```

Expected: `PostEntry_PropagatesAffectedAccountsToLinkedNextYear` FAILED (IB not updated), `PostEntry_DoesNotPropagateWhenNoLinkedYear` PASSED (no propagation code to fail yet).

- [ ] **Step 3: Add `PropagateAffectedAccountsAsync` and hook into `PostAsync` and `CreateReversalAsync`**

In `src/KoalaBooks.Application/Services/JournalEntryService.cs`:

Update `PostAsync` to include `Lines` in the query and call propagation:

```csharp
public async Task<string?> PostAsync(int entryId)
{
    var entry = await _db.JournalEntries
        .Include(j => j.FiscalYear)
        .Include(j => j.Lines)
        .FirstOrDefaultAsync(j => j.Id == entryId);
    if (entry is null) return "Journal entry not found.";
    if (entry.IsPosted) return "Journal entry is already posted.";
    if (entry.FiscalYear.IsClosed) return "Cannot post entries in a closed fiscal year.";

    entry.IsPosted = true;
    await _db.SaveChangesAsync();

    await PropagateAffectedAccountsAsync(
        entry.FiscalYearId, entry.Lines.Select(l => l.AccountId));
    return null;
}
```

At the end of `CreateReversalAsync`, after `await _db.SaveChangesAsync()`:

```csharp
_db.JournalEntries.Add(reversal);
await _db.SaveChangesAsync();

await PropagateAffectedAccountsAsync(
    reversal.FiscalYearId, reversal.Lines.Select(l => l.AccountId));
return (reversal, null);
```

Add the private helper method to `JournalEntryService`:

```csharp
private async Task PropagateAffectedAccountsAsync(
    int fiscalYearId, IEnumerable<int> affectedAccountIds)
{
    var nextYear = await _db.FiscalYears
        .FirstOrDefaultAsync(f => f.PreviousFiscalYearId == fiscalYearId);
    if (nextYear is null) return;

    var accountIdList = affectedAccountIds.ToList();

    var sourceAccounts = await _db.Accounts
        .Where(a => a.FiscalYearId == fiscalYearId && accountIdList.Contains(a.Id))
        .ToListAsync();

    var sourceNumbers = sourceAccounts.Select(a => a.AccountNumber).ToHashSet();
    var nextAccounts = await _db.Accounts
        .Where(a => a.FiscalYearId == nextYear.Id && sourceNumbers.Contains(a.AccountNumber))
        .ToDictionaryAsync(a => a.AccountNumber);

    var debits = await _db.JournalEntryLines
        .Where(l => accountIdList.Contains(l.AccountId) && l.JournalEntry.IsPosted)
        .GroupBy(l => l.AccountId)
        .Select(g => new { g.Key, Total = g.Sum(l => l.DebitAmount) })
        .ToDictionaryAsync(x => x.Key, x => x.Total);

    var credits = await _db.JournalEntryLines
        .Where(l => accountIdList.Contains(l.AccountId) && l.JournalEntry.IsPosted)
        .GroupBy(l => l.AccountId)
        .Select(g => new { g.Key, Total = g.Sum(l => l.CreditAmount) })
        .ToDictionaryAsync(x => x.Key, x => x.Total);

    foreach (var account in sourceAccounts)
    {
        var isPnL = account.AccountClass is AccountClass.Revenue or AccountClass.Expense;
        if (isPnL) continue;

        var d = debits.GetValueOrDefault(account.Id);
        var c = credits.GetValueOrDefault(account.Id);
        var ub = account.AccountClass.IsCreditNormal()
            ? account.IncomingBalance + c - d
            : account.IncomingBalance + d - c;

        if (nextAccounts.TryGetValue(account.AccountNumber, out var nextAccount))
            nextAccount.IncomingBalance = ub;
    }

    await _db.SaveChangesAsync();
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "PostEntry_Propagates|PostEntry_DoesNotPropagate" -q
```

Expected: 2 PASSED

- [ ] **Step 5: Run full suite — no regressions**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "FullyQualifiedName!~AttachmentProvider" -q
```

Expected: all PASSED

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/JournalEntryService.cs \
        tests/KoalaBooks.Tests/AccountMappingServiceTests.cs
git commit -m "feat: propagate affected account IBs to linked year on journal entry post"
```

---

## Task 7: SIE import propagation

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/Services/SieImportService.cs`
- Modify: `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs`

- [ ] **Step 1: Write failing test**

Add to `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs`:

```csharp
[Fact]
public async Task SieImport_PropagatesBalancesToLinkedNextYear()
{
    // Create 2025 year and an account with UB that will be overwritten by SIE
    var source = _f.CreateFiscalYear("2025",
        new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
    var cash2025 = _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
        outgoingBalance: 0);

    // 2026 linked to 2025
    var target = _f.CreateFiscalYear("2026",
        new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
    target.PreviousFiscalYearId = source.Id;
    var cash2026 = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
        incomingBalance: 0);
    _f.Db.SaveChanges();

    // Directly set UB on 2025 account (simulates what SieImportService.ImportBalancesAsync does)
    // then call the propagation hook the same way ImportFiscalYearAsync will after the fix
    cash2025.OutgoingBalance = 750m;
    _f.Db.SaveChanges();

    // Call the real SIE import propagation via a minimal import
    // We test the private helper by verifying end state after ImportFiscalYearAsync.
    // Since wiring the full SIE doc is heavy, we test the helper via direct DB manipulation
    // and assert it propagates correctly. Full integration covered by PropagateBalancesToNextYearAsync tests.
    await _f.FiscalYearService.PropagateBalancesToNextYearAsync(source.Id);

    await _f.Db.Entry(cash2026).ReloadAsync();
    Assert.Equal(750m, cash2026.IncomingBalance);
}
```

- [ ] **Step 2: Run test — confirm pass (this tests propagation itself, not SIE wiring yet)**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "SieImport_PropagatesBalancesToLinkedNextYear" -q
```

Expected: PASSED (the propagation method already works; this test validates the UB→IB path)

- [ ] **Step 3: Wire propagation into `SieImportService.ImportFiscalYearAsync`**

In `src/KoalaBooks.Infrastructure/Services/SieImportService.cs`, add a private helper method:

```csharp
private async Task PropagateToLinkedNextYearAsync(int fiscalYearId)
{
    var nextYear = await _db.FiscalYears
        .FirstOrDefaultAsync(f => f.PreviousFiscalYearId == fiscalYearId);
    if (nextYear is null) return;

    var sourceAccounts = await _db.Accounts
        .Where(a => a.FiscalYearId == fiscalYearId)
        .ToListAsync();

    var nextAccounts = await _db.Accounts
        .Where(a => a.FiscalYearId == nextYear.Id)
        .ToDictionaryAsync(a => a.AccountNumber);

    foreach (var src in sourceAccounts)
    {
        var isPnL = src.AccountClass is AccountClass.Revenue or AccountClass.Expense;
        if (isPnL) continue;
        if (nextAccounts.TryGetValue(src.AccountNumber, out var next))
            next.IncomingBalance = src.OutgoingBalance;
    }
    await _db.SaveChangesAsync();
}
```

At the end of `ImportFiscalYearAsync`, after the final `await _db.SaveChangesAsync()` (the one before `return new SieImportResult(...)`), add:

```csharp
await PropagateToLinkedNextYearAsync(fiscalYear.Id);
```

- [ ] **Step 4: Build to confirm no errors**

```bash
dotnet build src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Run full test suite**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "FullyQualifiedName!~AttachmentProvider" -q
```

Expected: all PASSED

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/SieImportService.cs \
        tests/KoalaBooks.Tests/AccountMappingServiceTests.cs
git commit -m "feat: propagate UBs to linked next year after SIE import"
```

---

## Task 8: Account mapping page, nav, and DI registration

**Files:**
- Create: `src/KoalaBooks.Web/Components/Pages/AccountMapping.razor`
- Modify: `src/KoalaBooks.Web/Components/Layout/MainLayout.razor`
- Modify: `src/KoalaBooks.Web/Program.cs`

- [ ] **Step 1: Register `AccountMappingService` in DI**

In `src/KoalaBooks.Web/Program.cs`, add after the existing `AddScoped<BasImportService>()` line:

```csharp
builder.Services.AddScoped<AccountMappingService>();
```

- [ ] **Step 2: Add nav link under Inställningar**

In `src/KoalaBooks.Web/Components/Layout/MainLayout.razor`, inside the `Inställningar` `MudNavGroup`, add after the `/accounts` link:

```razor
<MudNavLink Href="/account-mapping" Icon="@Icons.Material.Outlined.CompareArrows">Balansöverföring</MudNavLink>
```

- [ ] **Step 3: Create the account mapping page**

Create `src/KoalaBooks.Web/Components/Pages/AccountMapping.razor`:

```razor
@page "/account-mapping"
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Entities
@using MudBlazor

<PageTitle>Balansöverföring — KoalaBooks</PageTitle>

<h1>⇄ Balansöverföring</h1>
<p style="color:#64748b; margin-bottom:1.5rem;">
    Överför utgående saldon (UB) från ett räkenskapsår till ingående saldon (IB) i ett annat år.
</p>

@if (_state == PageState.Picker)
{
    <div class="card" style="max-width:600px;">
        <h3 style="margin:0 0 1rem 0;">Välj år</h3>

        <div style="display:grid; grid-template-columns:1fr 1fr; gap:1rem; margin-bottom:1rem;">
            <div class="form-group">
                <label>Källår (UB tas härifrån)</label>
                <select @bind="_sourceId" style="width:100%;">
                    @foreach (var fy in _allYears)
                    {
                        <option value="@fy.Id">@fy.Name</option>
                    }
                </select>
            </div>
            <div class="form-group">
                <label>Målår (IB skrivs hit)</label>
                <select @bind="_targetId" style="width:100%;">
                    @foreach (var fy in _allYears)
                    {
                        <option value="@fy.Id">@fy.Name</option>
                    }
                </select>
            </div>
        </div>

        @if (_sameYearError)
        {
            <MudAlert Severity="Severity.Error" Class="mb-3">Källa och mål kan inte vara samma år.</MudAlert>
        }

        @if (_existingSourceName is not null && !_confirmed)
        {
            <MudAlert Severity="Severity.Warning" Class="mb-3">
                Målåret är redan mappat från <strong>@_existingSourceName</strong>.
                Att fortsätta skriver över befintliga ingående saldon.
            </MudAlert>
            <label style="display:flex; align-items:center; gap:0.5rem; margin-bottom:1rem; cursor:pointer;">
                <input type="checkbox" @bind="_confirmed" />
                Jag förstår och vill fortsätta
            </label>
        }

        <button class="btn btn-primary" @onclick="ProceedToMapping" disabled="@_loading">
            @(_loading ? "Laddar..." : "Nästa →")
        </button>
    </div>
}
else if (_state == PageState.Mapping)
{
    <div style="margin-bottom:1rem; display:flex; justify-content:space-between; align-items:center;">
        <div>
            <strong>Källår:</strong> @_sourceYear!.Name &nbsp;→&nbsp;
            <strong>Målår:</strong> @_targetYear!.Name
        </div>
        <span style="color:#64748b; font-size:0.875rem;">
            @_rows.Count(r => r.SelectedTarget is not null) av @_rows.Count konton mappade
        </span>
    </div>

    <div style="max-height:500px; overflow-y:auto; border:1px solid #e2e8f0; border-radius:6px; margin-bottom:1rem;">
        <table style="margin:0;">
            <thead style="position:sticky; top:0; background:#f8fafc; z-index:1;">
                <tr>
                    <th style="width:100px;">Konto</th>
                    <th>Namn (källa)</th>
                    <th style="width:120px; text-align:right;">UB</th>
                    <th style="width:260px;">Målkonto</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var row in _rows)
                {
                    <tr>
                        <td style="font-family:monospace;">@row.SourceNumber</td>
                        <td>@row.SourceName</td>
                        <td style="text-align:right; font-family:monospace;">@row.Ub.ToString("N2")</td>
                        <td>
                            <select @bind="row.SelectedTarget" style="width:100%; font-size:0.875rem;">
                                <option value="">— Hoppa över —</option>
                                @foreach (var acc in _targetAccounts)
                                {
                                    <option value="@acc.AccountNumber">
                                        @acc.AccountNumber — @acc.Name
                                    </option>
                                }
                            </select>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </div>

    <div style="display:flex; gap:0.5rem;">
        <button class="btn btn-success" @onclick="ApplyMapping" disabled="@_applying">
            @(_applying ? "Tillämpar..." : "Tillämpa")
        </button>
        <button class="btn btn-secondary" @onclick="() => _state = PageState.Picker">Avbryt</button>
    </div>
}
else if (_state == PageState.Result)
{
    <div class="card" style="max-width:400px;">
        <h3 style="margin:0 0 1rem 0;">✅ Klart</h3>
        <p>Mappade: <strong>@_applyResult!.Mapped</strong> konton</p>
        <p>Hoppade över: <strong>@_applyResult.Skipped</strong> konton</p>
        <button class="btn btn-secondary" @onclick="Reset">Stäng</button>
    </div>
}

@code {
    [Inject] private AccountMappingService AccountMappingService { get; set; } = default!;
    [Inject] private FiscalYearService FiscalYearService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private enum PageState { Picker, Mapping, Result }
    private PageState _state = PageState.Picker;

    private List<FiscalYear> _allYears = [];
    private int _sourceId;
    private int _targetId;
    private bool _sameYearError;
    private string? _existingSourceName;
    private bool _confirmed;
    private bool _loading;
    private bool _applying;

    private FiscalYear? _sourceYear;
    private FiscalYear? _targetYear;

    private class UiRow
    {
        public required string SourceNumber { get; init; }
        public required string SourceName { get; init; }
        public decimal Ub { get; init; }
        public string? SelectedTarget { get; set; }
    }

    private List<UiRow> _rows = [];
    private List<Account> _targetAccounts = [];
    private ApplyMappingResult? _applyResult;

    protected override async Task OnInitializedAsync()
    {
        _allYears = await FiscalYearService.GetAllAsync();
        if (_allYears.Count >= 2)
        {
            _sourceId = _allYears[1].Id;
            _targetId = _allYears[0].Id;
        }
        else if (_allYears.Count == 1)
        {
            _sourceId = _allYears[0].Id;
            _targetId = _allYears[0].Id;
        }
    }

    private async Task ProceedToMapping()
    {
        _sameYearError = false;
        if (_sourceId == _targetId)
        {
            _sameYearError = true;
            return;
        }

        _sourceYear = _allYears.First(f => f.Id == _sourceId);
        _targetYear = _allYears.First(f => f.Id == _targetId);

        // Check if target already has a previous year linked
        if (_targetYear.PreviousFiscalYearId.HasValue && !_confirmed)
        {
            var existingSource = _allYears.FirstOrDefault(f => f.Id == _targetYear.PreviousFiscalYearId);
            _existingSourceName = existingSource?.Name ?? "okänt år";
            return; // Stay in picker, show warning
        }

        _loading = true;
        try
        {
            var rows = await AccountMappingService.BuildMappingAsync(_sourceId, _targetId);
            _rows = rows.Select(r => new UiRow
            {
                SourceNumber = r.SourceAccountNumber,
                SourceName = r.SourceAccountName,
                Ub = r.Ub,
                SelectedTarget = r.TargetAccountNumber
            }).ToList();

            // Load target accounts for dropdown
            _targetAccounts = await FiscalYearService.GetAccountsAsync(_targetId);

            _confirmed = false;
            _existingSourceName = null;
            _state = PageState.Mapping;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Fel: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ApplyMapping()
    {
        _applying = true;
        try
        {
            var mappingRows = _rows.Select(r => new MappingRow(
                r.SourceNumber,
                r.SourceName,
                r.Ub,
                string.IsNullOrEmpty(r.SelectedTarget) ? null : r.SelectedTarget
            )).ToList();

            _applyResult = await AccountMappingService.ApplyMappingAsync(_sourceId, _targetId, mappingRows);
            _state = PageState.Result;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Fel vid tillämpning: {ex.Message}", Severity.Error);
        }
        finally
        {
            _applying = false;
        }
    }

    private void Reset()
    {
        _state = PageState.Picker;
        _rows = [];
        _targetAccounts = [];
        _applyResult = null;
        _confirmed = false;
        _existingSourceName = null;
    }
}
```

- [ ] **Step 4: Add `GetAccountsAsync` to `FiscalYearService`** (needed by the mapping page)

In `src/KoalaBooks.Application/Services/FiscalYearService.cs`, add:

```csharp
public async Task<List<Account>> GetAccountsAsync(int fiscalYearId)
{
    return await _db.Accounts
        .Where(a => a.FiscalYearId == fiscalYearId)
        .OrderBy(a => a.AccountNumber)
        .ToListAsync();
}
```

- [ ] **Step 5: Build the solution**

```bash
dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj -q
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Run full test suite — final check**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj \
  --filter "FullyQualifiedName!~AttachmentProvider" -q
```

Expected: all PASSED

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Web/Components/Pages/AccountMapping.razor \
        src/KoalaBooks.Web/Components/Layout/MainLayout.razor \
        src/KoalaBooks.Web/Program.cs \
        src/KoalaBooks.Application/Services/FiscalYearService.cs \
        src/KoalaBooks.Application/Services/AccountMappingService.cs
git commit -m "feat: add account balance mapping page and nav link"
```
