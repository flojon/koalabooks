# Journal Entry Storno/Reversal Compliance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close issue #160 — harden the journal entry immutability/reversal (storno) pattern so posted vouchers can never be deleted (DB-level, not just app-level), reversals are traceable, and the reversal path is reachable from the REST API, not just the Blazor UI.

**Architecture:** `JournalEntryService.CreateReversalAsync` and `DeleteDraftAsync` already implement the correct app-level behavior (block delete of posted entries, create a negated reversal entry). This plan adds: (1) a `JournalEntryStatus` enum (`Draft/Posted/Reversed/Correction`) and a `SourceJournalEntryId` link so a reversal can be traced back to its original and the two are distinguishable in reports/UI; (2) an EF Core `SaveChanges` override in `AppDbContext` that rejects deleting any non-`Draft` entry regardless of which code path attempts it — a real backstop independent of `DeleteDraftAsync`; (3) a `POST /api/v1/journal-entries/{id}/reverse` endpoint so reversal isn't Blazor-UI-only; (4) UI/response wiring for the new `Status` field.

**Deliberate scope decision:** the issue asks to "replace" the `bool IsPosted` field. `IsPosted` is read in 20+ call sites across reporting/export code (`YearEndClosingService`, `AccountMappingService`, `SieExportService`, trial balance/general ledger/VAT queries) and constructed directly in 10+ existing tests. None of that code cares about the Draft/Posted/Reversed/Correction distinction — it only needs "is this entry part of the permanent, postable ledger." Ripping out `IsPosted` and replacing every read site with `Status != Draft` is a large, compliance-irrelevant blast radius for no behavioral gain. Instead, `Status` is added as the new source of truth for the concept the issue actually cares about (traceable reversal state), while `IsPosted` stays exactly as-is and stays in sync (set together at the two places that write it: `PostAsync` and `CreateReversalAsync`). The DB-level guard (the part of the issue with real compliance teeth) is keyed off `Status`, not `IsPosted`, so it does not depend on this invariant holding.

**Tech Stack:** .NET 10 / EF Core (Npgsql/PostgreSQL via Aspire), ASP.NET Core Web API, Blazor Server, xUnit + Testcontainers.PostgreSql.

## Global Constraints

- Target framework is `net10.0` everywhere — match existing project files, don't change `TargetFramework`.
- DB provider is PostgreSQL (Npgsql) in every environment (Aspire-provisioned in dev/prod, Testcontainers in tests) — no SQLite, despite what `README.md` says.
- Migrations are applied automatically on startup via `db.Database.MigrateAsync()` (`src/KoalaBooks.Web/Program.cs:192-213`), except in the `Testing` environment / xUnit `TestFixture`, which uses `Db.Database.EnsureCreated()` — any new migration must still be hand-correct (backfill SQL etc.) since `EnsureCreated()` will build the *current* model from scratch for tests, but the real Postgres databases replay the actual migration history.
- Enum values exposed over the REST API are serialized as JSON strings using `[property: JsonConverter(typeof(JsonStringEnumConverter))]` per-property (see `AccountResponse.cs:8`) — follow that exact pattern, there is no global `JsonStringEnumConverter` registered.
- Multi-tenant query filters exist on `JournalEntry`/`JournalEntryLine` (`AppDbContext.cs:49-53`) — nothing in this plan changes tenant scoping, don't touch those filters.
- Follow existing self-referencing FK convention: no navigation property is declared for the FK on the entity itself (see `FiscalYear.PreviousFiscalYearId`, which has no `PreviousFiscalYear` nav property; the relationship is configured via `entity.HasOne<FiscalYear>().WithMany()...` in `AppDbContext.cs:86-89`). Use the same pattern for `JournalEntry.SourceJournalEntryId`.

---

### Task 1: Add `JournalEntryStatus` enum and wire it onto `JournalEntry`

**Files:**
- Create: `src/KoalaBooks.Domain/Enums/JournalEntryStatus.cs`
- Modify: `src/KoalaBooks.Domain/Entities/JournalEntry.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs:1-4` (usings), `AppDbContext.cs:92-101` (`JournalEntry` entity config)
- Create: EF migration in `src/KoalaBooks.Infrastructure/Migrations/`
- Test: `tests/KoalaBooks.Tests/JournalEntryStatusTests.cs`

**Interfaces:**
- Produces: `JournalEntryStatus` enum (`Draft = 0, Posted = 1, Reversed = 2, Correction = 3`) in namespace `KoalaBooks.Domain.Enums`; `JournalEntry.Status` (`JournalEntryStatus`, defaults to `Draft`); `JournalEntry.SourceJournalEntryId` (`int?`). Later tasks (2, 3, 4, 5) read/write these.

- [ ] **Step 1: Write the failing test**

Create `tests/KoalaBooks.Tests/JournalEntryStatusTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class JournalEntryStatusTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public JournalEntryStatusTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task NewDraftEntry_DefaultsToStatusDraft()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m);
        var (created, error) = await _f.JournalEntryService.CreateAsync(entry);

        Assert.Null(error);
        Assert.NotNull(created);
        Assert.Equal(JournalEntryStatus.Draft, created.Status);

        var reloaded = await _f.Db.JournalEntries.FindAsync(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(JournalEntryStatus.Draft, reloaded!.Status);
        Assert.Null(reloaded.SourceJournalEntryId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~JournalEntryStatusTests`
Expected: FAIL with a compile error — `JournalEntry` does not contain a definition for `Status` (or `KoalaBooks.Domain.Enums.JournalEntryStatus` does not exist).

- [ ] **Step 3: Create the enum**

Create `src/KoalaBooks.Domain/Enums/JournalEntryStatus.cs`:

```csharp
namespace KoalaBooks.Domain.Enums;

public enum JournalEntryStatus
{
    Draft = 0,
    Posted = 1,
    Reversed = 2,
    Correction = 3
}
```

- [ ] **Step 4: Add `Status` and `SourceJournalEntryId` to the entity**

Modify `src/KoalaBooks.Domain/Entities/JournalEntry.cs` to:

```csharp
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Entities;

public class JournalEntry
{
    public int Id { get; set; }
    public int EntryNumber { get; set; }
    public DateOnly Date { get; set; }
    public required string Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsPosted { get; set; }
    public bool IsClosingEntry { get; set; }
    public JournalEntryStatus Status { get; set; } = JournalEntryStatus.Draft;
    public int? SourceJournalEntryId { get; set; }

    public int FiscalYearId { get; set; }
    public FiscalYear FiscalYear { get; set; } = null!;

    public List<JournalEntryLine> Lines { get; set; } = [];
    public List<Document> Documents { get; set; } = [];
}
```

- [ ] **Step 5: Add EF configuration**

In `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`, add the using at the top of the file (near the existing `using KoalaBooks.Domain.Entities;`):

```csharp
using KoalaBooks.Domain.Enums;
```

Then modify the `modelBuilder.Entity<JournalEntry>(entity => { ... })` block (currently `AppDbContext.cs:92-101`) to:

```csharp
modelBuilder.Entity<JournalEntry>(entity =>
{
    entity.HasIndex(j => new { j.FiscalYearId, j.EntryNumber }).IsUnique();
    entity.Property(j => j.Description).HasMaxLength(500);
    entity.Property(j => j.IsClosingEntry).HasDefaultValue(false);
    entity.Property(j => j.Status).HasDefaultValue(JournalEntryStatus.Draft);
    entity.HasIndex(j => j.SourceJournalEntryId);
    entity.HasOne<JournalEntry>()
          .WithMany()
          .HasForeignKey(j => j.SourceJournalEntryId)
          .OnDelete(DeleteBehavior.Restrict)
          .IsRequired(false);
    entity.HasOne(j => j.FiscalYear)
          .WithMany(f => f.JournalEntries)
          .HasForeignKey(j => j.FiscalYearId)
          .OnDelete(DeleteBehavior.Restrict);
});
```

- [ ] **Step 6: Generate the migration**

Run:
```bash
dotnet ef migrations add AddJournalEntryStatusAndReversalLink \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new files appear in `src/KoalaBooks.Infrastructure/Migrations/` named `<timestamp>_AddJournalEntryStatusAndReversalLink.cs` and `.Designer.cs`, and `AppDbContextModelSnapshot.cs` is updated.

- [ ] **Step 7: Add the data backfill to the generated migration**

Open the new `<timestamp>_AddJournalEntryStatusAndReversalLink.cs`. Its `Up()` method will contain an `AddColumn<int>(name: "Status", ...)` call generated from the `HasDefaultValue(JournalEntryStatus.Draft)` config — that default only applies to *new* rows, so existing posted entries would incorrectly read as `Draft` after migrating. Add a backfill `Sql` call right after the `Status` column is added (before the foreign key is created). The full `Up()`/`Down()` should end up looking like this (adjust only if EF generated different formatting — the column/index/FK names must match this shape):

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<int>(
        name: "Status",
        table: "JournalEntries",
        type: "integer",
        nullable: false,
        defaultValue: 0);

    migrationBuilder.AddColumn<int>(
        name: "SourceJournalEntryId",
        table: "JournalEntries",
        type: "integer",
        nullable: true);

    migrationBuilder.Sql(@"UPDATE ""JournalEntries"" SET ""Status"" = 1 WHERE ""IsPosted"" = true;");

    migrationBuilder.CreateIndex(
        name: "IX_JournalEntries_SourceJournalEntryId",
        table: "JournalEntries",
        column: "SourceJournalEntryId");

    migrationBuilder.AddForeignKey(
        name: "FK_JournalEntries_JournalEntries_SourceJournalEntryId",
        table: "JournalEntries",
        column: "SourceJournalEntryId",
        principalTable: "JournalEntries",
        principalColumn: "Id",
        onDelete: ReferentialAction.Restrict);
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropForeignKey(
        name: "FK_JournalEntries_JournalEntries_SourceJournalEntryId",
        table: "JournalEntries");

    migrationBuilder.DropIndex(
        name: "IX_JournalEntries_SourceJournalEntryId",
        table: "JournalEntries");

    migrationBuilder.DropColumn(
        name: "SourceJournalEntryId",
        table: "JournalEntries");

    migrationBuilder.DropColumn(
        name: "Status",
        table: "JournalEntries");
}
```

- [ ] **Step 8: Verify the migration applies cleanly**

Run: `dotnet ef database update --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web --connection "Host=localhost;Database=koalabooks_migration_check;Username=postgres;Password=postgres"` against a scratch local Postgres (or skip if no local Postgres is available — the xUnit run in Step 9 exercises the same model via `EnsureCreated()` and is the authoritative check for this task).

- [ ] **Step 9: Run test to verify it passes**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~JournalEntryStatusTests`
Expected: PASS (1 test).

- [ ] **Step 10: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, same count as before this task plus 1.

- [ ] **Step 11: Commit**

```bash
git add src/KoalaBooks.Domain/Enums/JournalEntryStatus.cs \
        src/KoalaBooks.Domain/Entities/JournalEntry.cs \
        src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
        src/KoalaBooks.Infrastructure/Migrations/ \
        tests/KoalaBooks.Tests/JournalEntryStatusTests.cs
git commit -m "feat: add JournalEntryStatus enum and SourceJournalEntryId link"
```

---

### Task 2: Wire `Status`/`SourceJournalEntryId` into `PostAsync` and `CreateReversalAsync`, block double reversal

**Files:**
- Modify: `src/KoalaBooks.Application/Services/JournalEntryService.cs:120-139` (`PostAsync`), `:159-204` (`CreateReversalAsync`)
- Test: `tests/KoalaBooks.Tests/JournalEntryStatusTests.cs` (extend from Task 1)

**Interfaces:**
- Consumes: `JournalEntryStatus` enum, `JournalEntry.Status`, `JournalEntry.SourceJournalEntryId` (Task 1).
- Produces: `PostAsync` sets `Status = Posted`. `CreateReversalAsync` sets the new reversal's `Status = Correction` and `SourceJournalEntryId = original.Id`, sets the original's `Status = Reversed`, and returns `(null, "Journal entry has already been reversed.")` if called twice on the same entry. Task 5 (UI) and the reverse endpoint (Task 4) rely on this error message text and on `Status` being populated on the returned entry.

- [ ] **Step 1: Write the failing tests**

Append to `tests/KoalaBooks.Tests/JournalEntryStatusTests.cs`:

```csharp
    [Fact]
    public async Task PostAsync_SetsStatusToPosted()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 300m);
        var (created, _) = await _f.JournalEntryService.CreateAsync(entry);

        await _f.JournalEntryService.PostAsync(created!.Id);

        var reloaded = await _f.Db.JournalEntries.FindAsync(created.Id);
        Assert.Equal(JournalEntryStatus.Posted, reloaded!.Status);
    }

    [Fact]
    public async Task CreateReversalAsync_MarksOriginalReversedAndLinksReversal()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);

        var (reversal, error) = await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Wrong amount");

        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.Equal(JournalEntryStatus.Correction, reversal!.Status);
        Assert.Equal(posted.Id, reversal.SourceJournalEntryId);

        var reloadedOriginal = await _f.Db.JournalEntries.FindAsync(posted.Id);
        Assert.Equal(JournalEntryStatus.Reversed, reloadedOriginal!.Status);
    }

    [Fact]
    public async Task CreateReversalAsync_AlreadyReversedEntry_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);
        await _f.JournalEntryService.CreateReversalAsync(posted.Id, "First reversal");

        var (secondReversal, error) = await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Second attempt");

        Assert.Null(secondReversal);
        Assert.NotNull(error);
        Assert.Contains("already been reversed", error, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~JournalEntryStatusTests`
Expected: FAIL — `PostAsync_SetsStatusToPosted` and `CreateReversalAsync_MarksOriginalReversedAndLinksReversal` fail because `Status` stays `Draft`/`Draft`; `CreateReversalAsync_AlreadyReversedEntry_ReturnsError` fails because a second reversal currently succeeds.

- [ ] **Step 3: Update `PostAsync`**

In `src/KoalaBooks.Application/Services/JournalEntryService.cs`, change:

```csharp
        entry.IsPosted = true;
        await _db.SaveChangesAsync();
```

(currently lines 133-134) to:

```csharp
        entry.IsPosted = true;
        entry.Status = JournalEntryStatus.Posted;
        await _db.SaveChangesAsync();
```

- [ ] **Step 4: Update `CreateReversalAsync`**

Replace the full method body (currently `JournalEntryService.cs:159-204`) with:

```csharp
    public async Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason)
    {
        var original = await _db.JournalEntries
            .Include(j => j.Lines)
            .Include(j => j.FiscalYear)
            .FirstOrDefaultAsync(j => j.Id == entryId);

        if (original is null)
            return (null, "Journal entry not found.");
        if (!original.IsPosted)
            return (null, "Can only reverse posted entries.");
        if (original.Status == JournalEntryStatus.Reversed)
            return (null, "Journal entry has already been reversed.");
        if (original.FiscalYear.IsClosed)
            return (null, "Cannot create reversals in a closed fiscal year.");

        var maxNumber = await _db.JournalEntries
            .Where(j => j.FiscalYearId == original.FiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber) ?? 0;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var reversalDate = today <= original.FiscalYear.EndDate && today >= original.FiscalYear.StartDate
            ? today
            : original.FiscalYear.EndDate;

        var reversal = new JournalEntry
        {
            EntryNumber = maxNumber + 1,
            FiscalYearId = original.FiscalYearId,
            Date = reversalDate,
            Description = $"Reversal of #{original.EntryNumber}: {reason}",
            CreatedAt = DateTime.UtcNow,
            IsPosted = true,
            Status = JournalEntryStatus.Correction,
            SourceJournalEntryId = original.Id,
            Lines = original.Lines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.CreditAmount,
                CreditAmount = l.DebitAmount
            }).ToList()
        };

        original.Status = JournalEntryStatus.Reversed;

        _db.JournalEntries.Add(reversal);
        await _db.SaveChangesAsync();

        await PropagateAffectedAccountsAsync(
            reversal.FiscalYearId, reversal.Lines.Select(l => l.AccountId));
        return (reversal, null);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~JournalEntryStatusTests`
Expected: PASS (4 tests total: the one from Task 1 plus these 3).

- [ ] **Step 6: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS. In particular `ReversalClosedYearTests` and `ReversalDateClampingTests` must still pass unchanged — they only assert on `IsPosted`/`Description`/`Date`, which are untouched.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Application/Services/JournalEntryService.cs \
        tests/KoalaBooks.Tests/JournalEntryStatusTests.cs
git commit -m "feat: link reversal entries to their source and block double reversal"
```

---

### Task 3: DB-level guard against deleting posted/reversed/correction entries

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`
- Test: `tests/KoalaBooks.Tests/JournalEntryDbGuardTests.cs`

**Interfaces:**
- Consumes: `JournalEntry.Status` (Task 1).
- Produces: `AppDbContext.SaveChanges()` / `SaveChangesAsync()` throw `InvalidOperationException("Cannot delete a posted, reversed, or correction journal entry. Create a reversal instead.")` if any tracked `JournalEntry` marked for deletion has `Status != JournalEntryStatus.Draft` — regardless of which calling code removed it. This is independent of and in addition to the existing app-level guard in `DeleteDraftAsync`.

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/JournalEntryDbGuardTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class JournalEntryDbGuardTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public JournalEntryDbGuardTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task DirectRemove_PostedEntry_ThrowsOnSaveChanges()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 500m);

        _f.Db.JournalEntries.Remove(posted);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task DirectRemove_ReversedEntry_ThrowsOnSaveChanges()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 500m);
        await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Oops");

        var reloaded = await _f.Db.JournalEntries.FindAsync(posted.Id);
        _f.Db.JournalEntries.Remove(reloaded!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task DirectRemove_DraftEntry_Succeeds()
    {
        var entry = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m);
        var (created, error) = await _f.JournalEntryService.CreateAsync(entry);
        Assert.Null(error);

        _f.Db.JournalEntries.Remove(created!);
        await _f.Db.SaveChangesAsync();

        var remaining = await _f.Db.JournalEntries.FindAsync(created!.Id);
        Assert.Null(remaining);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~JournalEntryDbGuardTests`
Expected: FAIL — `DirectRemove_PostedEntry_ThrowsOnSaveChanges` and `DirectRemove_ReversedEntry_ThrowsOnSaveChanges` fail because no exception is thrown today (the delete just succeeds).

- [ ] **Step 3: Add the `SaveChanges` guard**

In `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`, add these members inside the `AppDbContext` class, after the constructor and before `OnModelCreating`:

```csharp
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAgainstImmutableJournalEntryDeletion();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAgainstImmutableJournalEntryDeletion();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardAgainstImmutableJournalEntryDeletion()
    {
        var deletingImmutableEntries = ChangeTracker.Entries<JournalEntry>()
            .Any(e => e.State == EntityState.Deleted && e.Entity.Status != JournalEntryStatus.Draft);

        if (deletingImmutableEntries)
            throw new InvalidOperationException(
                "Cannot delete a posted, reversed, or correction journal entry. Create a reversal instead.");
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~JournalEntryDbGuardTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS. In particular `DeleteDraftAsyncTests` must still pass unchanged — every posted/closed-year scenario there already returns before reaching `SaveChangesAsync`, and the draft-delete scenarios still hit the new guard's `Any(...)` check but don't match it.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Data/AppDbContext.cs \
        tests/KoalaBooks.Tests/JournalEntryDbGuardTests.cs
git commit -m "feat: reject deletion of posted journal entries at the DbContext level"
```

---

### Task 4: Expose `POST /api/v1/journal-entries/{id}/reverse` and add `Status` to the API response

**Files:**
- Create: `src/KoalaBooks.Web/Models/Api/ReverseJournalEntryRequest.cs`
- Modify: `src/KoalaBooks.Web/Models/Api/JournalEntryResponse.cs`
- Modify: `src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs`
- Test: `tests/KoalaBooks.Tests/Api/ApiTests.cs`

**Interfaces:**
- Consumes: `JournalEntryService.CreateReversalAsync(int entryId, string reason)` (Task 2, unchanged signature), `JournalEntry.Status`/`SourceJournalEntryId` (Task 1).
- Produces: `POST /api/v1/journal-entries/{id}/reverse` — `201 Created` with a `JournalEntryResponse` body for the new reversal on success, `400` with the service's error text on failure, `404` if `id` doesn't resolve to an entry in the caller's tenant. `JournalEntryResponse` gains `Status` (string) and `SourceJournalEntryId` (`int?`) fields.

- [ ] **Step 1: Write the failing tests**

Append to `tests/KoalaBooks.Tests/Api/ApiTests.cs` (inside the `ApiTests` class, e.g. after `JournalEntries_GetById_CrossTenant_Returns404`):

```csharp
    [Fact]
    public async Task JournalEntries_Reverse_PostedEntry_ReturnsReversalLinkedToOriginal()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-09-01",
            description = "To be reversed",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 600m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 600m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var journalEntryService = scope.ServiceProvider.GetRequiredService<KoalaBooks.Application.Services.JournalEntryService>();
        var postError = await journalEntryService.PostAsync(entryId);
        Assert.Null(postError);

        var reverseResp = await client.PostAsJsonAsync($"/api/v1/journal-entries/{entryId}/reverse", new { reason = "Wrong account" });
        Assert.Equal(HttpStatusCode.Created, reverseResp.StatusCode);

        var reversal = await reverseResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Correction", reversal.GetProperty("status").GetString());
        Assert.Equal(entryId, reversal.GetProperty("sourceJournalEntryId").GetInt32());

        var originalResp = await client.GetAsync($"/api/v1/journal-entries/{entryId}");
        var original = await originalResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reversed", original.GetProperty("status").GetString());
    }

    [Fact]
    public async Task JournalEntries_Reverse_DraftEntry_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-09-02",
            description = "Still a draft",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 100m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var reverseResp = await client.PostAsJsonAsync($"/api/v1/journal-entries/{entryId}/reverse", new { reason = "Nope" });
        Assert.Equal(HttpStatusCode.BadRequest, reverseResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Reverse_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/journal-entries/999999/reverse", new { reason = "Nope" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~ApiTests.JournalEntries_Reverse`
Expected: FAIL with 404 (route not found) for all three — the endpoint doesn't exist yet.

- [ ] **Step 3: Add the request DTO**

Create `src/KoalaBooks.Web/Models/Api/ReverseJournalEntryRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class ReverseJournalEntryRequest
{
    [Required]
    public string Reason { get; init; } = "";
}
```

- [ ] **Step 4: Add `Status` and `SourceJournalEntryId` to the response DTO**

Modify `src/KoalaBooks.Web/Models/Api/JournalEntryResponse.cs` to:

```csharp
using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record JournalEntryResponse(
    int Id,
    int EntryNumber,
    DateOnly Date,
    string Description,
    bool IsPosted,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] JournalEntryStatus Status,
    int? SourceJournalEntryId,
    DateTime CreatedAt,
    List<JournalEntryLineResponse> Lines);
```

- [ ] **Step 5: Add the controller action and update `MapEntry`**

In `src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs`, add this action after `Delete` (currently ends at line 109):

```csharp
    [HttpPost("journal-entries/{id:int}/reverse")]
    [ProducesResponseType<JournalEntryResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reverse(int id, [FromBody] ReverseJournalEntryRequest request)
    {
        var entry = await _journalEntryService.GetByIdAsync(id);
        if (entry is null) return NotFound();

        var (reversal, error) = await _journalEntryService.CreateReversalAsync(id, request.Reason);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = reversal!.Id }, MapEntry(reversal));
    }
```

Then update `MapEntry` (currently `JournalEntriesController.cs:111-117`) to:

```csharp
    private static JournalEntryResponse MapEntry(JournalEntry e) =>
        new(e.Id, e.EntryNumber, e.Date, e.Description, e.IsPosted, e.Status, e.SourceJournalEntryId, e.CreatedAt,
            e.Lines.Select(l => new JournalEntryLineResponse(
                l.Id, l.AccountId,
                l.Account?.AccountNumber ?? "",
                l.Account?.Name ?? "",
                l.DebitAmount, l.CreditAmount)).ToList());
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~ApiTests.JournalEntries_Reverse`
Expected: PASS (3 tests).

- [ ] **Step 7: Run the full test suite to check for regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, including all other `ApiTests` (the `JournalEntryResponse` field order changed, but tests read by property name via `JsonElement.GetProperty`, not by position).

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Web/Models/Api/ReverseJournalEntryRequest.cs \
        src/KoalaBooks.Web/Models/Api/JournalEntryResponse.cs \
        src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs \
        tests/KoalaBooks.Tests/Api/ApiTests.cs
git commit -m "feat: expose POST /api/v1/journal-entries/{id}/reverse"
```

---

### Task 5: Reflect `Status` in the Blazor Journal UI and manually verify

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:124-133` (status badge), `:164-171` (reverse button gating)

**Interfaces:**
- Consumes: `JournalEntry.Status`, `JournalEntry.IsPosted` (Task 1/2). No new interfaces produced — this is the last task in the plan.

There is no Blazor component test harness in this repo (no bUnit package in `tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj`), so this task is verified manually in the browser per the project's UI-change convention.

- [ ] **Step 1: Update the status badge**

In `src/KoalaBooks.Components/Pages/Journal.razor`, replace the status cell (currently lines 124-133):

```razor
                <td>
                    @if (entry.IsPosted)
                    {
                        <span style="color:#16a34a;">✅ Bokförd</span>
                    }
                    else
                    {
                        <span style="color:#f59e0b;">📝 Utkast</span>
                    }
                </td>
```

with:

```razor
                <td>
                    @if (entry.Status == JournalEntryStatus.Reversed)
                    {
                        <span style="color:#6b7280;">↩️ Återförd</span>
                    }
                    else if (entry.Status == JournalEntryStatus.Correction)
                    {
                        <span style="color:#2563eb;">🔄 Rättelse</span>
                    }
                    else if (entry.IsPosted)
                    {
                        <span style="color:#16a34a;">✅ Bokförd</span>
                    }
                    else
                    {
                        <span style="color:#f59e0b;">📝 Utkast</span>
                    }
                </td>
```

- [ ] **Step 2: Hide the reversal button once an entry has already been reversed**

In the same file, change (currently line 164):

```razor
                    else if (entry.IsPosted)
```

to:

```razor
                    else if (entry.IsPosted && entry.Status != JournalEntryStatus.Reversed)
```

This mirrors the guard added in Task 2's `CreateReversalAsync` (`if (original.Status == JournalEntryStatus.Reversed) return (null, "Journal entry has already been reversed.")`) so the button never shows for an entry the service will already reject.

- [ ] **Step 3: Start the app**

Run (see the project's `aspire` skill for details):
```bash
cd src/KoalaBooks.AppHost
aspire run
```

Wait for the Aspire dashboard to report the `koalabooks-web` resource as running, then open its endpoint in a browser.

- [ ] **Step 4: Log in and exercise the golden path**

Log in with the seeded dev account: `admin@koalabooks.local` / `Admin123!` (auto-created on first run per `src/KoalaBooks.Web/Program.cs:215-235`). Navigate to `/journal`.

- Create a new draft journal entry — confirm it shows "📝 Utkast".
- Post it — confirm it shows "✅ Bokförd" and the "Återför" button appears.
- Click "Återför", enter a reason, confirm — verify: the original entry now shows "↩️ Återförd" and its "Återför" button is gone; a new entry appears showing "🔄 Rättelse".
- Confirm the original entry can no longer be deleted (there was already no delete button for posted entries before this plan — verify that's still the case) and that clicking around doesn't offer a way to reverse the already-reversed original a second time.

- [ ] **Step 5: Verify the DB-level guard doesn't block normal API/UI usage**

Still in the browser, create a second draft entry and delete it via the UI's existing draft-delete flow — confirm it still deletes successfully (this exercises the Task 3 guard's `Draft` path through the real app, not just the test suite).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "feat: show reversal status in the journal UI and hide reverse button once reversed"
```
