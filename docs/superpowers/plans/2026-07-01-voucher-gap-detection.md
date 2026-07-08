# Voucher Number Gap Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close issue #162 — detect gaps in the per-fiscal-year `JournalEntry.EntryNumber` sequence and require the user to document an explanation for each one (BFNAR 2013:2 §5) before the fiscal year can be closed.

**Architecture:** `EntryNumber` is assigned as `MAX(EntryNumber for FiscalYearId) + 1` (`JournalEntryService.CreateAsync`, `JournalEntryExtensions.NextEntryNumberAsync`), so the first entry in a fiscal year always gets `1` — gaps only ever arise from deleting a draft (`JournalEntryService.DeleteDraftAsync`; posted/reversed/correction entries can never be deleted, enforced at the `AppDbContext.SaveChanges` level). This plan adds: (1) a `VoucherGapExplanation` entity storing one explanation per `(FiscalYearId, MissingEntryNumber)`; (2) a `VoucherGapService` with `FindGapsAsync` (pure detection: missing integers between `1` and the highest `EntryNumber` present) and `GetUnexplainedGapsAsync`/`AddExplanationAsync`/`GetExplanationsAsync` (explanation bookkeeping); (3) a hook in `YearEndClosingService.ValidateForClosingAsync` — the single validation path shared by `PreviewClosingAsync` and `ExecuteClosingAsync` — that blocks closing while unexplained gaps remain; (4) a Blazor UI step on the existing `/fiscal-years` closing flow that lists each unexplained gap, collects a required free-text explanation for it, and shows a gap summary once the year's gaps are all documented.

**Deliberate scope decision:** the issue's file list mentions only Domain/Application/Infrastructure/UI files, not a REST API controller — this plan does not add one. `VoucherGapService` is a plain constructor-injected service exactly like `JournalEntryService`/`YearEndClosingService`, so a `POST /api/v1/...` endpoint can be added later without any rework here if needed.

**Tech Stack:** .NET 10 / EF Core (Npgsql/PostgreSQL via Aspire), Blazor Server (MudBlazor), xUnit + Testcontainers.PostgreSql.

## Global Constraints

- Target framework is `net10.0` everywhere — match existing project files, don't change `TargetFramework`.
- DB provider is PostgreSQL (Npgsql) in every environment (Aspire-provisioned in dev/prod, Testcontainers in tests) — no SQLite.
- Migrations are applied automatically on startup via `db.Database.MigrateAsync()` (`src/KoalaBooks.Web/Program.cs:206`); the xUnit `TestFixture` instead uses `Db.Database.EnsureCreated()`, which builds the *current* model from scratch — a new migration for a brand-new table needs no data backfill, so the generated migration can be used as EF produces it.
- Multi-tenant query filters exist on `JournalEntry`, `JournalEntryLine`, `SupplierInvoice`, `CustomerInvoice` etc., all keyed off `_currentUser.OrganisationId` via a `FiscalYear`/`FiscalYear`-of-something navigation (`AppDbContext.cs:66-89`). `VoucherGapExplanation` must get the same kind of filter, keyed through its `FiscalYear` navigation, exactly like `SupplierInvoice.cs`'s filter (`s.FiscalYear.OrganisationId`).
- Follow the `SupplierInvoice`/`CustomerInvoice` FK convention for a reference to `FiscalYear` that isn't the primary tenant scope owner: declare the `FiscalYear` navigation property on the child entity, but do **not** add a back-collection (`List<VoucherGapExplanation>`) on `FiscalYear` — `AppDbContext.cs:147-169` configures `SupplierInvoice.FiscalYear` this way (`entity.HasOne(s => s.FiscalYear).WithMany()...`), with no matching collection on `FiscalYear.cs`.
- There is no existing "who performed this action" field anywhere in the domain — `ExplainedBy` is captured directly from ASP.NET Core Identity via Blazor's built-in `AuthenticationStateProvider` (`(await AuthStateProvider.GetAuthenticationStateAsync()).User.Identity?.Name`), the same primitive `MainLayout.razor:20` already uses to display the signed-in user's name (`@context.User.Identity?.Name` inside `<AuthorizeView>`). No new interface or entity field is introduced for this.

---

### Task 1: Add the `VoucherGapExplanation` entity

**Files:**
- Create: `src/KoalaBooks.Domain/Entities/VoucherGapExplanation.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs:40-51` (DbSet), `:115-131` (entity config, added as a new block right after the `JournalEntryLine` config)
- Create: EF migration in `src/KoalaBooks.Infrastructure/Migrations/`
- Test: `tests/KoalaBooks.Tests/VoucherGapExplanationTests.cs`

**Interfaces:**
- Produces: `VoucherGapExplanation` entity (`Id`, `FiscalYearId`, `FiscalYear` nav, `MissingEntryNumber` (int), `Explanation` (required string), `ExplainedAt` (DateTime, defaults `DateTime.UtcNow`), `ExplainedBy` (required string)) in namespace `KoalaBooks.Domain.Entities`; unique index on `(FiscalYearId, MissingEntryNumber)`; `AppDbContext.VoucherGapExplanations` DbSet. Task 2 builds `VoucherGapService` on top of this DbSet.

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/VoucherGapExplanationTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class VoucherGapExplanationTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;

    public VoucherGapExplanationTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task SaveAndReload_RoundTripsAllFields()
    {
        var explanation = new VoucherGapExplanation
        {
            FiscalYearId = _fy.Id,
            MissingEntryNumber = 7,
            Explanation = "Utkast makulerat efter felaktig kontering.",
            ExplainedBy = "jonas@floden.co"
        };

        _f.Db.VoucherGapExplanations.Add(explanation);
        await _f.Db.SaveChangesAsync();

        var reloaded = await _f.Db.VoucherGapExplanations.FirstOrDefaultAsync(v => v.Id == explanation.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(_fy.Id, reloaded!.FiscalYearId);
        Assert.Equal(7, reloaded.MissingEntryNumber);
        Assert.Equal("Utkast makulerat efter felaktig kontering.", reloaded.Explanation);
        Assert.Equal("jonas@floden.co", reloaded.ExplainedBy);
    }

    [Fact]
    public async Task DuplicateFiscalYearAndMissingNumber_ThrowsOnSaveChanges()
    {
        _f.Db.VoucherGapExplanations.Add(new VoucherGapExplanation
        {
            FiscalYearId = _fy.Id,
            MissingEntryNumber = 3,
            Explanation = "First",
            ExplainedBy = "a@example.com"
        });
        await _f.Db.SaveChangesAsync();

        _f.Db.VoucherGapExplanations.Add(new VoucherGapExplanation
        {
            FiscalYearId = _fy.Id,
            MissingEntryNumber = 3,
            Explanation = "Second",
            ExplainedBy = "b@example.com"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _f.Db.SaveChangesAsync());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~VoucherGapExplanationTests`
Expected: FAIL with a compile error — `KoalaBooks.Domain.Entities.VoucherGapExplanation` does not exist (and `AppDbContext.VoucherGapExplanations` does not exist).

- [ ] **Step 3: Create the entity**

Create `src/KoalaBooks.Domain/Entities/VoucherGapExplanation.cs`:

```csharp
namespace KoalaBooks.Domain.Entities;

public class VoucherGapExplanation
{
    public int Id { get; set; }
    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;
    public int MissingEntryNumber { get; set; }
    public required string Explanation { get; set; }
    public DateTime ExplainedAt { get; set; } = DateTime.UtcNow;
    public required string ExplainedBy { get; set; }
}
```

- [ ] **Step 4: Register the DbSet and EF configuration**

In `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`, add a new DbSet line right after `JournalEntryLines` (currently line 44):

```csharp
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<VoucherGapExplanation> VoucherGapExplanations => Set<VoucherGapExplanation>();
```

Then add a new `modelBuilder.Entity<VoucherGapExplanation>(...)` block right after the existing `modelBuilder.Entity<JournalEntryLine>(entity => { ... });` block (currently ends at line 145, just before the `SupplierInvoice` block):

```csharp
        modelBuilder.Entity<VoucherGapExplanation>(entity =>
        {
            entity.HasQueryFilter(v => _currentUser.OrganisationId != null && v.FiscalYear.OrganisationId == _currentUser.OrganisationId);
            entity.HasIndex(v => new { v.FiscalYearId, v.MissingEntryNumber }).IsUnique();
            entity.Property(v => v.Explanation).HasMaxLength(1000);
            entity.Property(v => v.ExplainedBy).HasMaxLength(200);
            entity.HasOne(v => v.FiscalYear)
                  .WithMany()
                  .HasForeignKey(v => v.FiscalYearId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
```

- [ ] **Step 5: Generate the migration**

Run:
```bash
dotnet ef migrations add AddVoucherGapExplanation \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new files appear in `src/KoalaBooks.Infrastructure/Migrations/` named `<timestamp>_AddVoucherGapExplanation.cs` and `.Designer.cs`, creating a `VoucherGapExplanations` table with a unique index on `(FiscalYearId, MissingEntryNumber)` and a restrict-delete FK to `FiscalYears`; `AppDbContextModelSnapshot.cs` is updated. No manual edit of the generated migration is needed — this is a new table with no existing rows to backfill.

- [ ] **Step 6: Verify the migration applies cleanly**

Run: `dotnet ef database update --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web --connection "Host=localhost;Database=koalabooks_migration_check;Username=postgres;Password=postgres"` against a scratch local Postgres (or skip if no local Postgres is available — the xUnit run in Step 7 exercises the same model via `EnsureCreated()` and is the authoritative check for this task).

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~VoucherGapExplanationTests`
Expected: PASS (2 tests).

- [ ] **Step 8: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, same count as before this task plus 2.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Domain/Entities/VoucherGapExplanation.cs \
        src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
        src/KoalaBooks.Infrastructure/Migrations/ \
        tests/KoalaBooks.Tests/VoucherGapExplanationTests.cs
git commit -m "feat: add VoucherGapExplanation entity"
```

---

### Task 2: `VoucherGapService` — gap detection and explanation bookkeeping

**Files:**
- Create: `src/KoalaBooks.Application/Services/VoucherGapService.cs`
- Test: `tests/KoalaBooks.Tests/VoucherGapServiceTests.cs`

**Interfaces:**
- Consumes: `VoucherGapExplanation` entity, `AppDbContext.VoucherGapExplanations` (Task 1); `AppDbContext.JournalEntries`.
- Produces: `VoucherGapService(AppDbContext db)` with `Task<List<int>> FindGapsAsync(int fiscalYearId)` (missing integers strictly between `1` and the highest existing `EntryNumber`, empty list if no entries), `Task<List<int>> GetUnexplainedGapsAsync(int fiscalYearId)` (gaps minus already-explained numbers), `Task<string?> AddExplanationAsync(int fiscalYearId, int missingEntryNumber, string explanation, string explainedBy)` (upserts by `(FiscalYearId, MissingEntryNumber)`, returns an error string if `explanation` is blank or `missingEntryNumber` isn't currently a gap), `Task<List<VoucherGapExplanation>> GetExplanationsAsync(int fiscalYearId)`. Task 3 (`YearEndClosingService`) and Task 4 (Blazor UI) call all four methods.

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/VoucherGapServiceTests.cs`:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class VoucherGapServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;
    private readonly VoucherGapService _service;

    public VoucherGapServiceTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
        _service = new VoucherGapService(_f.Db);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task FindGapsAsync_NoEntries_ReturnsEmpty()
    {
        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Empty(gaps);
    }

    [Fact]
    public async Task FindGapsAsync_ConsecutiveEntries_ReturnsEmpty()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 100m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 200m);
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 300m);

        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Empty(gaps);
    }

    [Fact]
    public async Task FindGapsAsync_DeletedMiddleDraft_ReturnsGap()
    {
        var (created1, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        var (created2, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        var (created3, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m));
        Assert.Equal(2, created2!.EntryNumber);

        await _f.JournalEntryService.DeleteDraftAsync(created2.Id);

        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Equal([2], gaps);
    }

    [Fact]
    public async Task FindGapsAsync_MultipleGaps_ReturnsAllMissingInOrder()
    {
        for (var i = 0; i < 5; i++)
            await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m + i));

        var toDelete = await _f.Db.JournalEntries
            .Where(j => j.FiscalYearId == _fy.Id && (j.EntryNumber == 2 || j.EntryNumber == 4))
            .ToListAsync();
        foreach (var entry in toDelete)
            await _f.JournalEntryService.DeleteDraftAsync(entry.Id);

        var gaps = await _service.FindGapsAsync(_fy.Id);
        Assert.Equal([2, 4], gaps);
    }

    [Fact]
    public async Task GetUnexplainedGapsAsync_NoExplanations_ReturnsAllGaps()
    {
        await SeedGapOfTwoAsync();

        var unexplained = await _service.GetUnexplainedGapsAsync(_fy.Id);
        Assert.Equal([2], unexplained);
    }

    [Fact]
    public async Task GetUnexplainedGapsAsync_ExplainedGap_ExcludesIt()
    {
        await SeedGapOfTwoAsync();
        var error = await _service.AddExplanationAsync(_fy.Id, 2, "Utkast makulerat.", "jonas@floden.co");
        Assert.Null(error);

        var unexplained = await _service.GetUnexplainedGapsAsync(_fy.Id);
        Assert.Empty(unexplained);
    }

    [Fact]
    public async Task AddExplanationAsync_NumberIsNotAGap_ReturnsError()
    {
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 100m);

        var error = await _service.AddExplanationAsync(_fy.Id, 1, "Not a gap", "jonas@floden.co");

        Assert.NotNull(error);
        Assert.Contains("not a gap", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddExplanationAsync_EmptyExplanation_ReturnsError()
    {
        await SeedGapOfTwoAsync();

        var error = await _service.AddExplanationAsync(_fy.Id, 2, "   ", "jonas@floden.co");

        Assert.NotNull(error);
        Assert.Contains("explanation is required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddExplanationAsync_CalledTwiceForSameGap_UpdatesInPlace()
    {
        await SeedGapOfTwoAsync();
        await _service.AddExplanationAsync(_fy.Id, 2, "First reason", "jonas@floden.co");

        var error = await _service.AddExplanationAsync(_fy.Id, 2, "Corrected reason", "jonas@floden.co");
        Assert.Null(error);

        var explanations = await _service.GetExplanationsAsync(_fy.Id);
        var single = Assert.Single(explanations);
        Assert.Equal("Corrected reason", single.Explanation);
    }

    private async Task SeedGapOfTwoAsync()
    {
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        var (created2, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m));

        await _f.JournalEntryService.DeleteDraftAsync(created2!.Id);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~VoucherGapServiceTests`
Expected: FAIL with a compile error — `KoalaBooks.Application.Services.VoucherGapService` does not exist.

- [ ] **Step 3: Implement the service**

Create `src/KoalaBooks.Application/Services/VoucherGapService.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class VoucherGapService
{
    private readonly AppDbContext _db;

    public VoucherGapService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<int>> FindGapsAsync(int fiscalYearId)
    {
        var numbers = await _db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .Select(j => j.EntryNumber)
            .ToListAsync();

        if (numbers.Count == 0)
            return [];

        var present = numbers.ToHashSet();
        var max = numbers.Max();

        var gaps = new List<int>();
        for (var n = 1; n < max; n++)
        {
            if (!present.Contains(n))
                gaps.Add(n);
        }
        return gaps;
    }

    public async Task<List<int>> GetUnexplainedGapsAsync(int fiscalYearId)
    {
        var gaps = await FindGapsAsync(fiscalYearId);
        if (gaps.Count == 0)
            return gaps;

        var explained = await _db.VoucherGapExplanations
            .Where(v => v.FiscalYearId == fiscalYearId)
            .Select(v => v.MissingEntryNumber)
            .ToHashSetAsync();

        return gaps.Where(g => !explained.Contains(g)).ToList();
    }

    public async Task<string?> AddExplanationAsync(
        int fiscalYearId, int missingEntryNumber, string explanation, string explainedBy)
    {
        if (string.IsNullOrWhiteSpace(explanation))
            return "An explanation is required.";

        var gaps = await FindGapsAsync(fiscalYearId);
        if (!gaps.Contains(missingEntryNumber))
            return $"Entry number {missingEntryNumber} is not a gap in the sequence.";

        var existing = await _db.VoucherGapExplanations
            .FirstOrDefaultAsync(v => v.FiscalYearId == fiscalYearId && v.MissingEntryNumber == missingEntryNumber);

        if (existing is not null)
        {
            existing.Explanation = explanation;
            existing.ExplainedBy = explainedBy;
            existing.ExplainedAt = DateTime.UtcNow;
        }
        else
        {
            _db.VoucherGapExplanations.Add(new VoucherGapExplanation
            {
                FiscalYearId = fiscalYearId,
                MissingEntryNumber = missingEntryNumber,
                Explanation = explanation,
                ExplainedBy = explainedBy,
                ExplainedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        return null;
    }

    public async Task<List<VoucherGapExplanation>> GetExplanationsAsync(int fiscalYearId)
    {
        return await _db.VoucherGapExplanations
            .Where(v => v.FiscalYearId == fiscalYearId)
            .OrderBy(v => v.MissingEntryNumber)
            .ToListAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~VoucherGapServiceTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, same count as before this task plus 8.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/VoucherGapService.cs \
        tests/KoalaBooks.Tests/VoucherGapServiceTests.cs
git commit -m "feat: add VoucherGapService for gap detection and explanations"
```

---

### Task 3: Block fiscal year close while unexplained gaps remain

**Files:**
- Modify: `src/KoalaBooks.Application/Services/YearEndClosingService.cs:24-59` (constructor, `ValidateForClosingAsync`)
- Modify: `src/KoalaBooks.Web/Program.cs:101-103` (DI registration)
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs:18-53` (fixture wiring)
- Test: `tests/KoalaBooks.Tests/VoucherGapClosingValidationTests.cs`

**Interfaces:**
- Consumes: `VoucherGapService.GetUnexplainedGapsAsync(int fiscalYearId)`, `VoucherGapService.AddExplanationAsync(...)` (Task 2).
- Produces: `YearEndClosingService(AppDbContext db, FiscalYearService fiscalYearService, VoucherGapService voucherGapService)` — the constructor now takes a third parameter. `ValidateForClosingAsync` (and therefore `PreviewClosingAsync`/`ExecuteClosingAsync`, which both call it first) adds an error containing the literal substring `"BFNAR 2013:2"` to `ClosingValidationResult.Errors` whenever `GetUnexplainedGapsAsync` returns a non-empty list. Task 4 (Blazor UI) relies on `TestFixture.VoucherGapService` and on this exact substring being present/absent to decide what to render.

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/VoucherGapClosingValidationTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class VoucherGapClosingValidationTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public VoucherGapClosingValidationTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    private async Task<int> SeedUnexplainedGapAsync()
    {
        var (created1, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        var (created2, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        var (created3, _) = await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m));

        await _f.JournalEntryService.DeleteDraftAsync(created2!.Id);
        await _f.JournalEntryService.PostAsync(created1!.Id);
        await _f.JournalEntryService.PostAsync(created3!.Id);

        return created2.EntryNumber; // 2
    }

    [Fact]
    public async Task ValidateForClosingAsync_UnexplainedGap_ReturnsError()
    {
        await SeedUnexplainedGapAsync();

        var result = await _f.YearEndClosingService.ValidateForClosingAsync(_fy.Id);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BFNAR 2013:2"));
    }

    [Fact]
    public async Task ValidateForClosingAsync_ExplainedGap_NoGapError()
    {
        var missingNumber = await SeedUnexplainedGapAsync();
        var gapError = await _f.VoucherGapService.AddExplanationAsync(
            _fy.Id, missingNumber, "Utkast makulerat efter felkontering.", "jonas@floden.co");
        Assert.Null(gapError);

        var result = await _f.YearEndClosingService.ValidateForClosingAsync(_fy.Id);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Contains("BFNAR 2013:2"));
    }

    [Fact]
    public async Task ExecuteClosingAsync_UnexplainedGap_FiscalYearStaysOpen()
    {
        await SeedUnexplainedGapAsync();

        var result = await _f.YearEndClosingService.ExecuteClosingAsync(_fy.Id);

        Assert.False(result.Success);
        Assert.Contains("BFNAR 2013:2", result.Error);

        var reloaded = await _f.Db.FiscalYears.FindAsync(_fy.Id);
        Assert.False(reloaded!.IsClosed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~VoucherGapClosingValidationTests`
Expected: FAIL to compile — `TestFixture.VoucherGapService` does not exist yet.

- [ ] **Step 3: Wire `VoucherGapService` into `TestFixture`**

In `tests/KoalaBooks.Tests/TestFixture.cs`, add a property next to the other service properties (currently lines 18-23):

```csharp
    public AppDbContext Db { get; }
    public JournalEntryService JournalEntryService { get; }
    public FiscalYearService FiscalYearService { get; }
    public VoucherGapService VoucherGapService { get; }
    public YearEndClosingService YearEndClosingService { get; }
    public SieExportService SieExportService { get; }
    public SieImportService SieImportService { get; }
```

Then change the instantiation block (currently lines 49-53) to:

```csharp
        JournalEntryService = new JournalEntryService(Db);
        FiscalYearService = new FiscalYearService(Db, _currentUser);
        VoucherGapService = new VoucherGapService(Db);
        YearEndClosingService = new YearEndClosingService(Db, FiscalYearService, VoucherGapService);
        SieExportService = new SieExportService(Db);
        SieImportService = new SieImportService(Db, _currentUser);
```

- [ ] **Step 4: Run tests to verify they still fail correctly**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~VoucherGapClosingValidationTests`
Expected: FAIL to compile — `YearEndClosingService` does not have a 3-argument constructor yet.

- [ ] **Step 5: Update `YearEndClosingService`**

In `src/KoalaBooks.Application/Services/YearEndClosingService.cs`, change the constructor block (currently lines 26-33):

```csharp
    private readonly AppDbContext _db;
    private readonly FiscalYearService _fiscalYearService;

    public YearEndClosingService(AppDbContext db, FiscalYearService fiscalYearService)
    {
        _db = db;
        _fiscalYearService = fiscalYearService;
    }
```

to:

```csharp
    private readonly AppDbContext _db;
    private readonly FiscalYearService _fiscalYearService;
    private readonly VoucherGapService _voucherGapService;

    public YearEndClosingService(AppDbContext db, FiscalYearService fiscalYearService, VoucherGapService voucherGapService)
    {
        _db = db;
        _fiscalYearService = fiscalYearService;
        _voucherGapService = voucherGapService;
    }
```

Then change `ValidateForClosingAsync` (currently lines 35-59) to add the gap check right before the final `return`:

```csharp
    public async Task<ClosingValidationResult> ValidateForClosingAsync(int fiscalYearId)
    {
        var errors = new List<string>();

        var fiscalYear = await _db.FiscalYears.FirstOrDefaultAsync(f => f.Id == fiscalYearId);
        if (fiscalYear is null)
        {
            errors.Add("Fiscal year not found.");
            return new ClosingValidationResult(false, errors);
        }

        if (fiscalYear.IsClosed)
        {
            errors.Add("Fiscal year is already closed.");
        }

        var draftCount = await _db.JournalEntries
            .CountAsync(j => j.FiscalYearId == fiscalYearId && !j.IsPosted);
        if (draftCount > 0)
        {
            errors.Add($"Det finns {draftCount} ej bokförda verifikationer. Alla verifikationer måste bokföras innan bokslut.");
        }

        var unexplainedGaps = await _voucherGapService.GetUnexplainedGapsAsync(fiscalYearId);
        if (unexplainedGaps.Count > 0)
        {
            errors.Add(
                $"Det finns {unexplainedGaps.Count} lucka/luckor i verifikationsnumreringen (nr {string.Join(", ", unexplainedGaps)}) " +
                "som saknar förklaring enligt BFNAR 2013:2. Ange en förklaring för varje lucka innan bokslutet kan stängas.");
        }

        return new ClosingValidationResult(errors.Count == 0, errors);
    }
```

- [ ] **Step 6: Wire DI registration**

In `src/KoalaBooks.Web/Program.cs`, change (currently lines 101-103):

```csharp
builder.Services.AddScoped<JournalEntryService>();
builder.Services.AddScoped<SieExportService>();
builder.Services.AddScoped<YearEndClosingService>();
```

to:

```csharp
builder.Services.AddScoped<JournalEntryService>();
builder.Services.AddScoped<VoucherGapService>();
builder.Services.AddScoped<SieExportService>();
builder.Services.AddScoped<YearEndClosingService>();
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~VoucherGapClosingValidationTests`
Expected: PASS (3 tests).

- [ ] **Step 8: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS. In particular `YearEndClosingServiceTests`, `YearEndClosingLossTests`, `ClosingEntryFilterTests`, and `PostFiscalYearGuardTests` must still pass unchanged — none of their fixtures create a numbering gap, so `GetUnexplainedGapsAsync` returns an empty list for them and `ValidateForClosingAsync` behaves exactly as before.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Application/Services/YearEndClosingService.cs \
        src/KoalaBooks.Web/Program.cs \
        tests/KoalaBooks.Tests/TestFixture.cs \
        tests/KoalaBooks.Tests/VoucherGapClosingValidationTests.cs
git commit -m "feat: block fiscal year close while voucher number gaps are unexplained"
```

---

### Task 4: Blazor UI — collect gap explanations on the closing flow, manual verification

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/FiscalYears.razor`

**Interfaces:**
- Consumes: `VoucherGapService.GetUnexplainedGapsAsync`, `AddExplanationAsync`, `GetExplanationsAsync` (Task 2); `YearEndClosingService.PreviewClosingAsync`/`ExecuteClosingAsync` (unchanged signatures, now gap-aware per Task 3). No new interfaces produced — this is the last task in the plan.

There is no Blazor component test harness in this repo (no bUnit package in `tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj`), so this task is verified manually in the browser per the project's UI-change convention.

- [ ] **Step 1: Add injected services and page state**

In `src/KoalaBooks.Components/Pages/FiscalYears.razor`, add a using at the top (after the existing `@using MudBlazor` on line 5):

```razor
@using Microsoft.AspNetCore.Components.Authorization
```

Then add two injected services next to the existing ones (currently lines 158-161):

```razor
    [Inject] private FiscalYearService FiscalYearService { get; set; } = default!;
    [Inject] private YearEndClosingService YearEndClosingService { get; set; } = default!;
    [Inject] private VoucherGapService VoucherGapService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private BasImportService BasImportService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
```

And add new fields next to the existing closing-flow state (currently lines 171-174):

```razor
    // Closing flow state
    private int? _closingFiscalYearId;
    private ClosingPreview? _closingPreview;
    private bool _closingBusy;
    private List<int> _unexplainedGaps = [];
    private Dictionary<int, string> _gapExplanationDrafts = [];
    private string? _gapExplanationError;
    private List<VoucherGapExplanation> _gapExplanations = [];
```

- [ ] **Step 2: Make `StartClosing` check for unexplained gaps first**

Replace `StartClosing` (currently lines 235-241):

```csharp
    private async Task StartClosing(int fiscalYearId)
    {
        _closingFiscalYearId = fiscalYearId;
        _closingBusy = true;
        _closingPreview = await YearEndClosingService.PreviewClosingAsync(fiscalYearId);
        _closingBusy = false;
    }
```

with:

```csharp
    private async Task StartClosing(int fiscalYearId)
    {
        _closingFiscalYearId = fiscalYearId;
        _closingBusy = true;
        _closingPreview = null;
        _gapExplanationError = null;

        _unexplainedGaps = await VoucherGapService.GetUnexplainedGapsAsync(fiscalYearId);
        if (_unexplainedGaps.Count == 0)
        {
            _gapExplanations = await VoucherGapService.GetExplanationsAsync(fiscalYearId);
            _closingPreview = await YearEndClosingService.PreviewClosingAsync(fiscalYearId);
        }
        else
        {
            _gapExplanationDrafts = _unexplainedGaps.ToDictionary(n => n, n => "");
        }
        _closingBusy = false;
    }
```

- [ ] **Step 3: Add `SaveGapExplanations` and extend `CancelClosing`**

Replace `CancelClosing` (currently lines 243-247):

```csharp
    private void CancelClosing()
    {
        _closingFiscalYearId = null;
        _closingPreview = null;
    }
```

with:

```csharp
    private void CancelClosing()
    {
        _closingFiscalYearId = null;
        _closingPreview = null;
        _unexplainedGaps = [];
        _gapExplanationDrafts = [];
        _gapExplanationError = null;
    }

    private async Task SaveGapExplanations(int fiscalYearId)
    {
        _gapExplanationError = null;
        if (_gapExplanationDrafts.Values.Any(string.IsNullOrWhiteSpace))
        {
            _gapExplanationError = "Alla luckor måste ha en förklaring.";
            return;
        }

        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var who = authState.User.Identity?.Name ?? "Okänd";

        foreach (var (missingNumber, explanation) in _gapExplanationDrafts)
        {
            var error = await VoucherGapService.AddExplanationAsync(fiscalYearId, missingNumber, explanation.Trim(), who);
            if (error is not null)
            {
                _gapExplanationError = error;
                return;
            }
        }

        _unexplainedGaps = await VoucherGapService.GetUnexplainedGapsAsync(fiscalYearId);
        if (_unexplainedGaps.Count == 0)
        {
            _gapExplanations = await VoucherGapService.GetExplanationsAsync(fiscalYearId);
            _closingPreview = await YearEndClosingService.PreviewClosingAsync(fiscalYearId);
        }
    }
```

- [ ] **Step 4: Add the gap-explanation form and gap summary to the markup**

Replace the closing panel `<tr>` block (currently lines 83-147):

```razor
            @if (_closingFiscalYearId == fy.Id)
            {
                <tr>
                    <td colspan="5">
                        <div class="card" style="margin:0.5rem 0;">
                            @if (_closingBusy)
                            {
                                <p>⏳ Laddar...</p>
                            }
                            else if (_unexplainedGaps.Count > 0)
                            {
                                <h4>⚠️ Luckor i verifikationsnumreringen</h4>
                                <p>BFNAR 2013:2 kräver en förklaring för varje lucka i nummerserien innan bokslutet kan stängas.</p>
                                @foreach (var number in _unexplainedGaps)
                                {
                                    <div class="form-group">
                                        <label>Verifikat #@number saknas — förklaring</label>
                                        <input type="text" @bind="_gapExplanationDrafts[number]" placeholder="T.ex. Makulerat utkast" />
                                    </div>
                                }
                                @if (_gapExplanationError is not null)
                                {
                                    <MudAlert Severity="Severity.Error" Class="mt-2">@_gapExplanationError</MudAlert>
                                }
                                <div style="margin-top:1rem; display:flex; gap:0.5rem;">
                                    <button class="btn btn-sm btn-primary" @onclick="() => SaveGapExplanations(fy.Id)">Spara förklaringar</button>
                                    <button class="btn btn-sm btn-secondary" @onclick="CancelClosing">Avbryt</button>
                                </div>
                            }
                            else if (_closingPreview is not null)
                            {
                                @if (!_closingPreview.IsValid)
                                {
                                    <h4>⚠️ Kan inte stänga bokslut</h4>
                                    <ul>
                                        @foreach (var err in _closingPreview.Errors)
                                        {
                                            <li style="color:#dc2626;">@err</li>
                                        }
                                    </ul>
                                    <button class="btn btn-sm btn-secondary" @onclick="CancelClosing">Avbryt</button>
                                }
                                else
                                {
                                    <h4>📋 Förhandsvisning — Bokslut @fy.Name</h4>
                                    @if (_gapExplanations.Count > 0)
                                    {
                                        <p>ℹ️ @_gapExplanations.Count lucka/luckor i nummerserien är förklarade och dokumenterade.</p>
                                    }
                                    <div style="display:flex; gap:2rem; margin-bottom:1rem;">
                                        <span>Intäkter: <strong>@_closingPreview.TotalRevenue.ToString("N2")</strong></span>
                                        <span>Kostnader: <strong>@_closingPreview.TotalExpenses.ToString("N2")</strong></span>
                                        <span>Resultat: <strong>@_closingPreview.NetResult.ToString("N2")</strong></span>
                                    </div>
                                    @foreach (var entry in _closingPreview.Entries)
                                    {
                                        <p><strong>@entry.Description</strong></p>
                                        <table style="margin-bottom:0.5rem;">
                                            <thead>
                                                <tr>
                                                    <th>Konto</th>
                                                    <th>Namn</th>
                                                    <th style="width:120px;">Debet</th>
                                                    <th style="width:120px;">Kredit</th>
                                                </tr>
                                            </thead>
                                            <tbody>
                                                @foreach (var line in entry.Lines)
                                                {
                                                    <tr>
                                                        <td>@line.AccountNumber</td>
                                                        <td>@line.AccountName</td>
                                                        <td>@(line.Debit != 0 ? line.Debit.ToString("N2") : "")</td>
                                                        <td>@(line.Credit != 0 ? line.Credit.ToString("N2") : "")</td>
                                                    </tr>
                                                }
                                            </tbody>
                                        </table>
                                    }
                                    <div style="margin-top:1rem; display:flex; gap:0.5rem;">
                                        <button class="btn btn-sm btn-danger" @onclick="() => ConfirmClosing(fy.Id)">✅ Bekräfta och stäng</button>
                                        <button class="btn btn-sm btn-secondary" @onclick="CancelClosing">Avbryt</button>
                                    </div>
                                }
                            }
                        </div>
                    </td>
                </tr>
            }
```

- [ ] **Step 5: Start the app**

Run (see the project's `aspire` skill for details):
```bash
cd src/KoalaBooks.AppHost
aspire run
```

Wait for the Aspire dashboard to report the `koalabooks-web` resource as running, then open its endpoint in a browser.

- [ ] **Step 6: Log in and create a numbering gap**

Log in with the seeded dev account: `admin@koalabooks.local` / `Admin123!`. Navigate to `/journal`, create three draft entries, post the first and third, and delete the second while it's still a draft (this reproduces the exact gap scenario the plan is built for — a deleted draft leaving a hole in the sequence).

- [ ] **Step 7: Exercise the golden path**

Navigate to `/fiscal-years` and click "Stäng bokslut" on the fiscal year used above.

- Confirm the gap-explanation form appears, listing the missing entry number, instead of the usual preview.
- Try clicking "Spara förklaringar" with the field left empty — confirm "Alla luckor måste ha en förklaring." is shown and nothing is saved.
- Fill in an explanation and click "Spara förklaringar" — confirm the panel now shows the normal closing preview, including the "ℹ️ ... lucka/luckor ... förklarade" summary line.
- Click "Avbryt", then click "Stäng bokslut" again on the same fiscal year — confirm the gap form does **not** reappear (the explanation persisted) and the preview shows immediately.
- Complete the close by clicking "✅ Bekräfta och stäng" — confirm it succeeds as before.

- [ ] **Step 8: Verify the block holds via the API-equivalent path**

Still with a fresh fiscal year, create a new gap (repeat Step 6 on a different, still-open fiscal year) and click "Stäng bokslut" without filling in an explanation, then close the browser tab without saving. Reopen `/fiscal-years` and click "Stäng bokslut" again — confirm the gap form reappears (the explanation was never persisted, so the block is still active on a fresh page load, not just client-side state).

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Components/Pages/FiscalYears.razor
git commit -m "feat: require voucher gap explanations before fiscal year close"
```
