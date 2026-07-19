# Fiscal Year Resolution (#283) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the ambiguous `FiscalYearService.GetActiveAsync()` (which silently picks the open fiscal year with the latest `StartDate`, wrong whenever two fiscal years are open at once) with explicit, intent-specific fiscal-year resolution across every caller, per GitHub issue #283.

**Architecture:** `FiscalYearService` gains three replacement methods — `GetForDateAsync(date)` (unambiguous because `CreateAsync` already forbids overlapping date ranges), `GetDefaultFiscalYearAsync()` (today's year, with an explicit documented fallback), and `GetOpenFiscalYearsAsync()`. Callers split into three groups: (1) organisation-wide work queues (Todo, Review, Inbox, and their nav badges) drop fiscal-year filtering entirely and query by `OrganisationId`, gaining an optional in-page fiscal-year filter defaulting to "All"; (2) single-record, date-driven flows (`ClassifyDocumentDialog`) resolve from the record's own date; (3) single-fiscal-year-scoped pages (SupplierInvoices, BankImport, CustomerInvoices, Accounts, and the existing report pages) get/keep an explicit per-page selector, seeded from a new scoped `FiscalYearSelectionContext` (last fiscal year the user explicitly picked on any page in this group, in-memory per session) falling back to `GetDefaultFiscalYearAsync()`. `GetActiveAsync()` is deleted once every caller is migrated.

**Tech Stack:** .NET / Blazor Server (MudBlazor components), EF Core + PostgreSQL, xUnit + a real Postgres-backed `TestFixture` for service tests, bUnit + NSubstitute for component tests.

## Global Constraints

- No new mutable "active"/"current" flag on `FiscalYear` or `Organisation` — resolution must stay derivable from `StartDate`/`EndDate`/`IsClosed`, per the ticket's explicit rejection of enforcing a single open fiscal year.
- No global fiscal-year selector component — per-page selection only, optionally seeded from the new scoped `FiscalYearSelectionContext`.
- `GetActiveAsync()` stays in `IFiscalYearService` until every caller in this plan is migrated (Tasks 4–15), then is deleted in Task 16 — keeps every intermediate task independently buildable.
- Todo, Review, and Inbox become organisation-wide by default with an in-page fiscal-year filter (not a selector that changes the underlying scope) — confirmed direction from the issue discussion, extending beyond the literal ticket text (which only named Review and Inbox).
- `AccountMapping.razor` is out of scope: it never calls `GetActiveAsync()` and already uses explicit source/target year pickers — no ambiguity to fix.
- **Correction (2026-07-19): PR #278 is merged into `main`** (verified via `gh pr view 278` — `state: MERGED`, `baseRefName: main`). The four endpoints it added are real production callers, not out-of-scope WASM-branch-only code as this plan originally stated:
  - `GET api/v1/fiscal-years/active` (`FiscalYearsController.GetActive()`) calls `GetActiveAsync()` directly — this must be added to the caller list Task 16's "confirm no remaining callers" grep checks, and per the ticket's own suggestion should be renamed to `/default` backed by `GetDefaultFiscalYearAsync()` (not folded into this plan's existing tasks — needs its own task, not yet written).
    **Coordinate with #122's program plan (PR #301) before writing that task:** `FiscalYearsController.cs` is also Agent B's file in #301 (adding `create`, `get-accounts-for-year`, `propagate-balances` actions), landing independently of this plan. Whichever of the two PRs (this plan's future `GetActive`→`/default` rename task, or #301's Agent B stream) merges second will need a routine rebase on the same file — not a design conflict, just a file-level heads-up. Check `main` for Agent B's state before writing the rename task so it isn't scoped against a stale version of the controller.
  - `GET api/v1/fiscal-years/{fiscalYearId}/journal-entries/draft-count` (`JournalEntriesController`), `.../bank-transactions/unmatched-count` (`BankTransactionsController`), `.../supplier-invoices/unpaid-count` (`SupplierInvoicesController`) are all `fiscalYearId`-scoped only, with no organisation-scoped counterpart — same gap flagged for the WASM MainLayout badge work once it wires up to these endpoints, using this plan's Task 2 methods (`CountDraftsForOrganisationAsync` etc.) as the backing implementation. Not yet scheduled as a task.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/KoalaBooks.Application/Services/IFiscalYearService.cs`, `FiscalYearService.cs` | Add `GetForDateAsync`, `GetDefaultFiscalYearAsync`, `GetOpenFiscalYearsAsync`; remove `GetActiveAsync` (Task 16) |
| `src/KoalaBooks.Application/Services/IJournalEntryService.cs`, `JournalEntryService.cs` | Add `GetDraftsForOrganisationAsync`, `CountDraftsForOrganisationAsync` |
| `src/KoalaBooks.Domain/Interfaces/IBankImportService.cs`, `src/KoalaBooks.Infrastructure/Services/BankImportService.cs` | Add `GetUnmatchedForOrganisationAsync`, `CountUnmatchedForOrganisationAsync` |
| `src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs`, `SupplierInvoiceService.cs` | Add `GetAllForOrganisationAsync`, `CountUnpaidForOrganisationAsync` |
| `src/KoalaBooks.Application/Services/IDocumentService.cs`, `DocumentService.cs` | Extend `GetPendingAsync`/`GetPendingCountAsync` with an optional fiscal-year date-range / undated filter |
| `src/KoalaBooks.Application/Services/FiscalYearSelectionContext.cs` *(new)* | Scoped, in-memory "last explicitly selected fiscal year" shared by the transactional + reporting page clusters |
| `src/KoalaBooks.Web/Program.cs` | Register `FiscalYearSelectionContext` as scoped |
| `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor` | Resolve fiscal year from the document's date, not "active" |
| `src/KoalaBooks.Components/Pages/Customers.razor` | Drop fiscal-year dependency; use `ICurrentUser.OrganisationId` |
| `src/KoalaBooks.Components/Pages/Home.razor` | Use `GetDefaultFiscalYearAsync` |
| `src/KoalaBooks.Components/Layout/MainLayout.razor` | Badges become organisation-wide counts; drop `IFiscalYearService` dependency entirely |
| `src/KoalaBooks.Components/Pages/Review.razor` | Organisation-wide drafts + in-page fiscal-year filter (default "All") |
| `src/KoalaBooks.Components/Pages/Todo.razor` | Organisation-wide unmatched/unpaid + in-page fiscal-year filter (default "All") |
| `src/KoalaBooks.Components/Pages/Inbox.razor` | Add fiscal-year filter (All / specific year / Undated) alongside the existing type filter |
| `src/KoalaBooks.Components/Pages/SupplierInvoices.razor`, `BankImport.razor`, `CustomerInvoices.razor`, `Accounts.razor` | Add a real fiscal-year selector (none exists today), wired to `FiscalYearSelectionContext` |
| `src/KoalaBooks.Components/Pages/TrialBalance.razor`, `GeneralLedger.razor`, `VatReport.razor`, `IncomeStatement.razor`, `BalanceSheet.razor`, `Journal.razor` | Replace ad-hoc "latest non-closed" default with `FiscalYearSelectionContext` + `GetDefaultFiscalYearAsync`; write selection back to the context |
| `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs`, `JournalEntryServiceTests.cs` (new methods), etc. | Unit tests per new service method |
| `tests/KoalaBooks.ComponentTests/HomeTests.cs`, `PreviewDocumentDialogTests.cs` | Update `GetActiveAsync` stubs to the new methods |

---

### Task 1: `FiscalYearService` — date-based resolution methods

**Files:**
- Modify: `src/KoalaBooks.Application/Services/IFiscalYearService.cs`
- Modify: `src/KoalaBooks.Application/Services/FiscalYearService.cs:1-39`
- Test: `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs`

**Interfaces:**
- Produces: `Task<FiscalYear?> GetForDateAsync(DateOnly date)` — the fiscal year whose `[StartDate, EndDate]` range contains `date`, regardless of `IsClosed` (unambiguous: `CreateAsync` already rejects overlapping ranges). `null` if no year covers that date.
- Produces: `Task<FiscalYear?> GetDefaultFiscalYearAsync()` — `GetForDateAsync(today)`; if that's `null`, falls back to the most-recently-started **open** fiscal year (`!IsClosed`, `OrderByDescending(StartDate)`, first). `null` only if there are no open fiscal years at all. Callers that receive a result via the fallback path have no way to distinguish it from an exact match from this method alone — that's fine for badges/defaults, but Task 8 (ClassifyDocumentDialog) intentionally does NOT use this method, because a single record's own date must resolve exactly or be flagged, not fall back silently.
- Produces: `Task<List<FiscalYear>> GetOpenFiscalYearsAsync()` — all `!IsClosed` years, `OrderByDescending(StartDate)`.
- `GetActiveAsync()` stays untouched in this task.

- [ ] **Step 1: Write the failing tests**

Add to `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs` (append inside the class, before the final `}`):

```csharp
    [Fact]
    public async Task GetForDateAsync_DateInsideRange_ReturnsThatYear()
    {
        _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2026, 6, 15));

        Assert.NotNull(result);
        Assert.Equal(fy2026.Id, result.Id);
    }

    [Fact]
    public async Task GetForDateAsync_TwoOpenYears_PicksTheOneContainingTheDate()
    {
        // Regression test for #283: two simultaneously open fiscal years must not
        // collapse to "whichever started later" — the date decides, not IsClosed.
        var fy2025 = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2025, 3, 1));

        Assert.NotNull(result);
        Assert.Equal(fy2025.Id, result.Id);
    }

    [Fact]
    public async Task GetForDateAsync_NoYearCoversDate_ReturnsNull()
    {
        _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2030, 1, 1));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetForDateAsync_ClosedYearCoveringDate_IsStillReturned()
    {
        var closed = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);

        var result = await _f.FiscalYearService.GetForDateAsync(new DateOnly(2025, 6, 1));

        Assert.NotNull(result);
        Assert.Equal(closed.Id, result.Id);
    }

    [Fact]
    public async Task GetDefaultFiscalYearAsync_TodayCoveredByAYear_ReturnsIt()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fy = _f.CreateFiscalYear("Current",
            today.AddMonths(-1), today.AddMonths(1));

        var result = await _f.FiscalYearService.GetDefaultFiscalYearAsync();

        Assert.NotNull(result);
        Assert.Equal(fy.Id, result.Id);
    }

    [Fact]
    public async Task GetDefaultFiscalYearAsync_NoYearCoversToday_FallsBackToLatestOpenYear()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Gap: no fiscal year covers "today", but there are two open years, one older.
        _f.CreateFiscalYear("Older open",
            today.AddYears(-2), today.AddYears(-1).AddDays(-1));
        var newerOpen = _f.CreateFiscalYear("Newer open",
            today.AddYears(1), today.AddYears(2));

        var result = await _f.FiscalYearService.GetDefaultFiscalYearAsync();

        Assert.NotNull(result);
        Assert.Equal(newerOpen.Id, result.Id);
    }

    [Fact]
    public async Task GetDefaultFiscalYearAsync_NoOpenYearsAtAll_ReturnsNull()
    {
        _f.CreateFiscalYear("Closed",
            new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31), isClosed: true);

        var result = await _f.FiscalYearService.GetDefaultFiscalYearAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOpenFiscalYearsAsync_ExcludesClosedYears_OrderedByStartDateDescending()
    {
        _f.CreateFiscalYear("2024", new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), isClosed: true);
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var result = await _f.FiscalYearService.GetOpenFiscalYearsAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(fy2026.Id, result[0].Id);
        Assert.Equal(fy2025.Id, result[1].Id);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~FiscalYearServiceTests"`
Expected: FAIL — `GetForDateAsync`/`GetDefaultFiscalYearAsync`/`GetOpenFiscalYearsAsync` don't exist yet (compile error).

- [ ] **Step 3: Add the methods to the interface**

In `src/KoalaBooks.Application/Services/IFiscalYearService.cs`, add after `Task<FiscalYear?> GetActiveAsync();`:

```csharp
    Task<FiscalYear?> GetForDateAsync(DateOnly date);
    Task<FiscalYear?> GetDefaultFiscalYearAsync();
    Task<List<FiscalYear>> GetOpenFiscalYearsAsync();
```

- [ ] **Step 4: Implement in `FiscalYearService`**

In `src/KoalaBooks.Application/Services/FiscalYearService.cs`, add after the existing `GetActiveAsync()` method (after line 39):

```csharp
    public async Task<FiscalYear?> GetForDateAsync(DateOnly date)
    {
        return await _db.FiscalYears
            .Where(f => f.StartDate <= date && f.EndDate >= date)
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }

    public async Task<FiscalYear?> GetDefaultFiscalYearAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return await GetForDateAsync(today).ConfigureAwait(false)
            ?? await _db.FiscalYears
                .Where(f => !f.IsClosed)
                .OrderByDescending(f => f.StartDate)
                .FirstOrDefaultAsync().ConfigureAwait(false);
    }

    public async Task<List<FiscalYear>> GetOpenFiscalYearsAsync()
    {
        return await _db.FiscalYears
            .Where(f => !f.IsClosed)
            .OrderByDescending(f => f.StartDate)
            .ToListAsync().ConfigureAwait(false);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~FiscalYearServiceTests"`
Expected: PASS (all, including the pre-existing tests).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/IFiscalYearService.cs src/KoalaBooks.Application/Services/FiscalYearService.cs tests/KoalaBooks.Tests/FiscalYearServiceTests.cs
git commit -m "Add date-based fiscal year resolution methods (#283)"
```

---

### Task 2: Organisation-wide query methods for work-queue services

**Files:**
- Modify: `src/KoalaBooks.Application/Services/IJournalEntryService.cs`, `JournalEntryService.cs:22-37`
- Modify: `src/KoalaBooks.Domain/Interfaces/IBankImportService.cs`, `src/KoalaBooks.Infrastructure/Services/BankImportService.cs:250-263`
- Modify: `src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs`, `SupplierInvoiceService.cs:17-27`
- Test: `tests/KoalaBooks.Tests/JournalEntryServiceTests.cs`, a new `tests/KoalaBooks.Tests/BankImportServiceOrganisationScopeTests.cs`, a new `tests/KoalaBooks.Tests/SupplierInvoiceServiceOrganisationScopeTests.cs`

**Interfaces:**
- Produces: `Task<List<JournalEntry>> GetDraftsForOrganisationAsync(int organisationId)`, `Task<int> CountDraftsForOrganisationAsync(int organisationId)`
- Produces: `Task<List<BankTransaction>> GetUnmatchedForOrganisationAsync(int organisationId)`, `Task<int> CountUnmatchedForOrganisationAsync(int organisationId)`
- Produces: `Task<List<SupplierInvoice>> GetAllForOrganisationAsync(int organisationId)`, `Task<int> CountUnpaidForOrganisationAsync(int organisationId)`

- [ ] **Step 1: Write the failing tests**

Check whether `tests/KoalaBooks.Tests/JournalEntryServiceTests.cs` exists:

Run: `grep -l "class JournalEntryServiceTests" tests/KoalaBooks.Tests/*.cs`

If it exists, append these inside the class. If not, this step notes where the assertions go regardless — add to whichever file already covers `JournalEntryService` against `TestFixture`. Use the fixture's `_f.MakeEntry(fiscalYearId, debitAccountId, creditAccountId, amount, date, description)` helper (see `TestFixture.cs`) and `_f.CreateFiscalYear`/`_f.OrganisationId`/`_f.CreateAccount`.

```csharp
    [Fact]
    public async Task GetDraftsForOrganisationAsync_SpansMultipleOpenFiscalYears()
    {
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var acc1 = _f.CreateAccount(fy2025.Id, "1910", "Kassa");
        var acc2 = _f.CreateAccount(fy2025.Id, "2440", "Lev.skulder");
        var acc3 = _f.CreateAccount(fy2026.Id, "1910", "Kassa");
        var acc4 = _f.CreateAccount(fy2026.Id, "2440", "Lev.skulder");

        var draft2025 = _f.MakeEntry(fy2025.Id, acc1.Id, acc2.Id, 100, new DateOnly(2025, 6, 1));
        var draft2026 = _f.MakeEntry(fy2026.Id, acc3.Id, acc4.Id, 200, new DateOnly(2026, 6, 1));
        _f.Db.JournalEntries.AddRange(draft2025, draft2026);
        await _f.Db.SaveChangesAsync();

        var drafts = await _f.JournalEntryService.GetDraftsForOrganisationAsync(_f.OrganisationId);
        var count = await _f.JournalEntryService.CountDraftsForOrganisationAsync(_f.OrganisationId);

        Assert.Equal(2, drafts.Count);
        Assert.Equal(2, count);
        Assert.Contains(drafts, d => d.Id == draft2025.Id);
        Assert.Contains(drafts, d => d.Id == draft2026.Id);
    }

    [Fact]
    public async Task GetDraftsForOrganisationAsync_ExcludesPostedEntries()
    {
        var fy = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var acc1 = _f.CreateAccount(fy.Id, "1910", "Kassa");
        var acc2 = _f.CreateAccount(fy.Id, "2440", "Lev.skulder");
        var posted = _f.MakeEntry(fy.Id, acc1.Id, acc2.Id, 100, new DateOnly(2026, 6, 1));
        posted.IsPosted = true;
        _f.Db.JournalEntries.Add(posted);
        await _f.Db.SaveChangesAsync();

        var drafts = await _f.JournalEntryService.GetDraftsForOrganisationAsync(_f.OrganisationId);

        Assert.Empty(drafts);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~GetDraftsForOrganisationAsync"`
Expected: FAIL (compile error — method doesn't exist).

- [ ] **Step 3: Implement `GetDraftsForOrganisationAsync`/`CountDraftsForOrganisationAsync`**

Add to `src/KoalaBooks.Application/Services/IJournalEntryService.cs` after `Task<int> CountDraftsAsync(int fiscalYearId);`:

```csharp
    Task<List<JournalEntry>> GetDraftsForOrganisationAsync(int organisationId);
    Task<int> CountDraftsForOrganisationAsync(int organisationId);
```

Add to `src/KoalaBooks.Application/Services/JournalEntryService.cs` after the existing `CountDraftsAsync` method (line 37):

```csharp
    public async Task<List<JournalEntry>> GetDraftsForOrganisationAsync(int organisationId)
    {
        return await _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.FiscalYear.OrganisationId == organisationId && !j.IsPosted)
            .OrderBy(j => j.Date)
            .ToListAsync().ConfigureAwait(false);
    }

    public Task<int> CountDraftsForOrganisationAsync(int organisationId) =>
        _db.JournalEntries.CountAsync(j => j.FiscalYear.OrganisationId == organisationId && !j.IsPosted);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~GetDraftsForOrganisationAsync"`
Expected: PASS.

- [ ] **Step 5: Write the failing bank-import test**

Create `tests/KoalaBooks.Tests/BankImportServiceOrganisationScopeTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

public class BankImportServiceOrganisationScopeTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly BankImportService _svc;

    public BankImportServiceOrganisationScopeTests()
    {
        _f = new TestFixture();
        _svc = new BankImportService(_f.Db, TestFixture.MakeTenant(_f.OrganisationId));
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task GetUnmatchedForOrganisationAsync_SpansMultipleOpenFiscalYears()
    {
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var acc2025 = _f.CreateAccount(fy2025.Id, "1930", "Bank");
        var acc2026 = _f.CreateAccount(fy2026.Id, "1930", "Bank");

        _f.Db.BankTransactions.AddRange(
            new BankTransaction { OrganisationId = _f.OrganisationId, AccountId = acc2025.Id, Date = new DateOnly(2025, 6, 1), Amount = 100, Description = "tx1", Status = BankTransactionStatus.Unmatched },
            new BankTransaction { OrganisationId = _f.OrganisationId, AccountId = acc2026.Id, Date = new DateOnly(2026, 6, 1), Amount = 200, Description = "tx2", Status = BankTransactionStatus.Unmatched },
            new BankTransaction { OrganisationId = _f.OrganisationId, AccountId = acc2026.Id, Date = new DateOnly(2026, 7, 1), Amount = 300, Description = "tx3", Status = BankTransactionStatus.Matched });
        await _f.Db.SaveChangesAsync();

        var unmatched = await _svc.GetUnmatchedForOrganisationAsync(_f.OrganisationId);
        var count = await _svc.CountUnmatchedForOrganisationAsync(_f.OrganisationId);

        Assert.Equal(2, unmatched.Count);
        Assert.Equal(2, count);
    }
}
```

- [ ] **Step 6: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~BankImportServiceOrganisationScopeTests"`
Expected: FAIL (compile error).

- [ ] **Step 7: Implement `GetUnmatchedForOrganisationAsync`/`CountUnmatchedForOrganisationAsync`**

Add to `src/KoalaBooks.Domain/Interfaces/IBankImportService.cs` after `Task<int> CountUnmatchedAsync(int fiscalYearId);`:

```csharp
    Task<int> CountUnmatchedForOrganisationAsync(int organisationId);
    Task<List<BankTransaction>> GetUnmatchedForOrganisationAsync(int organisationId);
```

Add to `src/KoalaBooks.Infrastructure/Services/BankImportService.cs` after the existing `GetUnmatchedAsync` method (line 263):

```csharp
    public Task<int> CountUnmatchedForOrganisationAsync(int organisationId) =>
        _db.BankTransactions.CountAsync(b =>
            b.OrganisationId == organisationId &&
            b.Status == BankTransactionStatus.Unmatched);

    public async Task<List<BankTransaction>> GetUnmatchedForOrganisationAsync(int organisationId)
    {
        return await _db.BankTransactions
            .Include(b => b.Account)
            .Where(b => b.OrganisationId == organisationId && b.Status == BankTransactionStatus.Unmatched)
            .OrderBy(b => b.Date)
            .ThenBy(b => b.Id)
            .ToListAsync().ConfigureAwait(false);
    }
```

- [ ] **Step 8: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~BankImportServiceOrganisationScopeTests"`
Expected: PASS.

- [ ] **Step 9: Write the failing supplier-invoice test**

Create `tests/KoalaBooks.Tests/SupplierInvoiceServiceOrganisationScopeTests.cs`:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class SupplierInvoiceServiceOrganisationScopeTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly SupplierInvoiceService _svc;

    public SupplierInvoiceServiceOrganisationScopeTests()
    {
        _f = new TestFixture();
        _svc = new SupplierInvoiceService(_f.Db);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task GetAllForOrganisationAsync_SpansMultipleOpenFiscalYears()
    {
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        _f.Db.SupplierInvoices.AddRange(
            new SupplierInvoice { FiscalYearId = fy2025.Id, SupplierName = "A", InvoiceDate = new DateOnly(2025, 6, 1), DueDate = new DateOnly(2025, 7, 1), TotalAmount = 100, IsPaid = false },
            new SupplierInvoice { FiscalYearId = fy2026.Id, SupplierName = "B", InvoiceDate = new DateOnly(2026, 6, 1), DueDate = new DateOnly(2026, 7, 1), TotalAmount = 200, IsPaid = false },
            new SupplierInvoice { FiscalYearId = fy2026.Id, SupplierName = "C", InvoiceDate = new DateOnly(2026, 6, 1), DueDate = new DateOnly(2026, 7, 1), TotalAmount = 300, IsPaid = true });
        await _f.Db.SaveChangesAsync();

        var all = await _svc.GetAllForOrganisationAsync(_f.OrganisationId);
        var unpaidCount = await _svc.CountUnpaidForOrganisationAsync(_f.OrganisationId);

        Assert.Equal(3, all.Count);
        Assert.Equal(2, unpaidCount);
    }
}
```

- [ ] **Step 10: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~SupplierInvoiceServiceOrganisationScopeTests"`
Expected: FAIL (compile error).

- [ ] **Step 11: Implement `GetAllForOrganisationAsync`/`CountUnpaidForOrganisationAsync`**

Add to `src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs` after `Task<int> CountUnpaidAsync(int fiscalYearId);`:

```csharp
    Task<int> CountUnpaidForOrganisationAsync(int organisationId);
    Task<List<SupplierInvoice>> GetAllForOrganisationAsync(int organisationId);
```

Add to `src/KoalaBooks.Application/Services/SupplierInvoiceService.cs` after the existing `GetAllAsync` method (line 27):

```csharp
    public Task<int> CountUnpaidForOrganisationAsync(int organisationId) =>
        _db.SupplierInvoices.CountAsync(s => s.FiscalYear.OrganisationId == organisationId && !s.IsPaid);

    public async Task<List<SupplierInvoice>> GetAllForOrganisationAsync(int organisationId)
    {
        return await _db.SupplierInvoices
            .Include(s => s.JournalEntry)
            .Include(s => s.PaymentJournalEntry)
            .Where(s => s.FiscalYear.OrganisationId == organisationId)
            .OrderByDescending(s => s.InvoiceDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync().ConfigureAwait(false);
    }
```

- [ ] **Step 12: Run to verify pass, then run the full suite**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~SupplierInvoiceServiceOrganisationScopeTests"`
Expected: PASS.

Run: `dotnet build`
Expected: 0 errors (confirms `SupplierInvoice`/`BankTransaction`/`JournalEntry` all resolve `.FiscalYear`/`.OrganisationId` navigation correctly).

- [ ] **Step 13: Commit**

```bash
git add src/KoalaBooks.Application/Services/IJournalEntryService.cs src/KoalaBooks.Application/Services/JournalEntryService.cs \
        src/KoalaBooks.Domain/Interfaces/IBankImportService.cs src/KoalaBooks.Infrastructure/Services/BankImportService.cs \
        src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs src/KoalaBooks.Application/Services/SupplierInvoiceService.cs \
        tests/KoalaBooks.Tests/JournalEntryServiceTests.cs tests/KoalaBooks.Tests/BankImportServiceOrganisationScopeTests.cs tests/KoalaBooks.Tests/SupplierInvoiceServiceOrganisationScopeTests.cs
git commit -m "Add organisation-scoped queries for drafts, unmatched transactions, unpaid invoices (#283)"
```

---

### Task 3: `FiscalYearSelectionContext` — scoped cross-page memory

**Files:**
- Create: `src/KoalaBooks.Application/Services/FiscalYearSelectionContext.cs`
- Modify: `src/KoalaBooks.Web/Program.cs:145` (register after `IFiscalYearService`)
- Test: `tests/KoalaBooks.Tests/FiscalYearSelectionContextTests.cs`

**Interfaces:**
- Produces: `class FiscalYearSelectionContext { int? LastSelectedFiscalYearId { get; }; void Set(int fiscalYearId); }`, registered scoped (one instance per Blazor Server circuit/session, in-memory only, never persisted).
- Consumed by: Tasks 11–15 (SupplierInvoices, BankImport, CustomerInvoices, Accounts, and the six report pages).

- [ ] **Step 1: Write the failing test**

Create `tests/KoalaBooks.Tests/FiscalYearSelectionContextTests.cs`:

```csharp
using KoalaBooks.Application.Services;

namespace KoalaBooks.Tests;

public class FiscalYearSelectionContextTests
{
    [Fact]
    public void NewContext_HasNoSelection()
    {
        var ctx = new FiscalYearSelectionContext();

        Assert.Null(ctx.LastSelectedFiscalYearId);
    }

    [Fact]
    public void Set_ThenRead_ReturnsTheValueThatWasSet()
    {
        var ctx = new FiscalYearSelectionContext();

        ctx.Set(42);

        Assert.Equal(42, ctx.LastSelectedFiscalYearId);
    }

    [Fact]
    public void Set_Twice_LatestValueWins()
    {
        var ctx = new FiscalYearSelectionContext();

        ctx.Set(1);
        ctx.Set(2);

        Assert.Equal(2, ctx.LastSelectedFiscalYearId);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~FiscalYearSelectionContextTests"`
Expected: FAIL — `FiscalYearSelectionContext` doesn't exist (compile error).

- [ ] **Step 3: Implement**

Create `src/KoalaBooks.Application/Services/FiscalYearSelectionContext.cs`:

```csharp
namespace KoalaBooks.Application.Services;

// Scoped (per Blazor Server circuit): remembers the last fiscal year a user explicitly
// picked on any page in the transactional/reporting page cluster, so navigating between
// them (e.g. BankImport -> GeneralLedger) doesn't reset to "today's year" every time.
// Deliberately NOT a global source of truth - pages seed their default from this, but the
// user can always override it locally, and organisation-wide pages (Todo/Review/Inbox)
// never read from it.
public sealed class FiscalYearSelectionContext
{
    public int? LastSelectedFiscalYearId { get; private set; }

    public void Set(int fiscalYearId) => LastSelectedFiscalYearId = fiscalYearId;
}
```

- [ ] **Step 4: Register as scoped**

In `src/KoalaBooks.Web/Program.cs`, add immediately after line 145 (`builder.Services.AddScoped<IFiscalYearService, FiscalYearService>();`):

```csharp
builder.Services.AddScoped<FiscalYearSelectionContext>();
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~FiscalYearSelectionContextTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/FiscalYearSelectionContext.cs src/KoalaBooks.Web/Program.cs tests/KoalaBooks.Tests/FiscalYearSelectionContextTests.cs
git commit -m "Add scoped FiscalYearSelectionContext for cross-page fiscal year memory (#283)"
```

---

### Task 4: `Customers.razor` — drop fiscal-year dependency

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Customers.razor:14-19,123-161`

**Interfaces:**
- Consumes: `ICurrentUser.OrganisationId` (`src/KoalaBooks.Domain/Interfaces/ICurrentUser.cs`, already `int? OrganisationId { get; }`).

**Rationale:** Customers.razor only ever used `fy.OrganisationId` — it has no fiscal-year-scoped data at all. Depending on `GetActiveAsync()` meant the page broke ("Inget aktivt räkenskapsår") whenever fiscal-year resolution failed, for a page that doesn't need a fiscal year.

- [ ] **Step 1: Replace the fiscal-year gate and organisation lookup**

In `src/KoalaBooks.Components/Pages/Customers.razor`, replace lines 14-19:

```razor
else if (_noFiscalYear)
{
    <MudAlert Severity="Severity.Info">Inget aktivt räkenskapsår. <a href="/fiscal-years">Skapa ett</a> först.</MudAlert>
}
else
{
```

with:

```razor
else
{
```

Replace the `@code` block's injects (lines 124-127):

```razor
    [Inject] private ICustomerService CustomerService { get; set; } = default!;
    [Inject] private IFiscalYearService FiscalYearService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
```

with:

```razor
    [Inject] private ICustomerService CustomerService { get; set; } = default!;
    [Inject] private ICurrentUser CurrentUser { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
```

Add `@using KoalaBooks.Domain.Interfaces` to the top `@using` block (after `@using KoalaBooks.Domain.Entities` at line 3).

Remove `private bool _noFiscalYear;` (line 145).

Replace `OnInitializedAsync` (lines 147-161):

```razor
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        var fy = await FiscalYearService.GetActiveAsync();
        if (fy is not null)
        {
            _organisationId = fy.OrganisationId;
            _customers = await CustomerService.GetAllAsync(_organisationId);
        }
        else
        {
            _noFiscalYear = true;
        }
        _isLoading = false;
    }
```

with:

```razor
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _organisationId = CurrentUser.OrganisationId ?? throw new InvalidOperationException("No active tenant.");
        _customers = await CustomerService.GetAllAsync(_organisationId);
        _isLoading = false;
    }
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Manually verify the page**

Run: `dotnet run --project src/KoalaBooks.Web` (or use the project's `run` skill), sign in, navigate to `/customers`. Confirm the customer list loads and the "no fiscal year" message never appears, even with a test organisation that has zero fiscal years.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Customers.razor
git commit -m "Customers.razor: use ICurrentUser.OrganisationId instead of a fiscal year lookup (#283)"
```

---

### Task 5: `Home.razor` — use `GetDefaultFiscalYearAsync`

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Home.razor:55-62`
- Modify: `tests/KoalaBooks.ComponentTests/HomeTests.cs:29,47`

**Interfaces:**
- Consumes: `IFiscalYearService.GetDefaultFiscalYearAsync()` (Task 1).

- [ ] **Step 1: Update the component tests to the new method (write the failing state first)**

In `tests/KoalaBooks.ComponentTests/HomeTests.cs`, replace line 29:

```csharp
        _fiscalYearService.GetActiveAsync().Returns((FiscalYear?)null);
```

with:

```csharp
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns((FiscalYear?)null);
```

and replace line 47:

```csharp
        _fiscalYearService.GetActiveAsync().Returns(fiscalYear);
```

with:

```csharp
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(fiscalYear);
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~HomeTests"`
Expected: FAIL — `Home.razor` still calls `GetActiveAsync()`, which the substitute no longer stubs, so it returns `null` unconditionally and `ActiveFiscalYear_ShowsNameAndDashboardStats` fails.

- [ ] **Step 3: Update `Home.razor`**

In `src/KoalaBooks.Components/Pages/Home.razor`, replace line 57:

```csharp
        _activeFiscalYear = await FiscalYearService.GetActiveAsync();
```

with:

```csharp
        _activeFiscalYear = await FiscalYearService.GetDefaultFiscalYearAsync();
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~HomeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Home.razor tests/KoalaBooks.ComponentTests/HomeTests.cs
git commit -m "Home.razor: resolve dashboard fiscal year via GetDefaultFiscalYearAsync (#283)"
```

---

### Task 6: `MainLayout.razor` badges — organisation-wide counts

**Files:**
- Modify: `src/KoalaBooks.Components/Layout/MainLayout.razor:147-202`

**Interfaces:**
- Consumes: `IBankImportService.CountUnmatchedForOrganisationAsync`, `ISupplierInvoiceService.CountUnpaidForOrganisationAsync`, `IJournalEntryService.CountDraftsForOrganisationAsync` (Task 2), `ICurrentUser.OrganisationId`.

**Rationale:** The draft badge counts the exact same data Review.razor will show after Task 8 (organisation-wide drafts). Leaving the badge fiscal-year-scoped while Review goes organisation-wide would make the nav badge and the page it links to disagree. The todo badge mirrors Todo.razor, which goes organisation-wide in Task 9 for the same reason. Both badges drop `IFiscalYearService` entirely — they only need `ICurrentUser.OrganisationId`.

- [ ] **Step 1: Replace `LoadTodoCountAsync` and `LoadDraftCountAsync`**

In `src/KoalaBooks.Components/Layout/MainLayout.razor`, replace lines 147-202:

```csharp
    private async Task LoadTodoCountAsync()
    {
        if (_loadingTodoCount) return;
        _loadingTodoCount = true;
        try
        {
            // Use a dedicated scope so this background query doesn't share a DbContext
            // with the page's OnInitializedAsync, which would cause concurrent-operation exceptions.
            await using var scope = ScopeFactory.CreateAsyncScope();
            var fySvc = scope.ServiceProvider.GetRequiredService<IFiscalYearService>();
            var bankSvc = scope.ServiceProvider.GetRequiredService<IBankImportService>();
            var invSvc = scope.ServiceProvider.GetRequiredService<ISupplierInvoiceService>();

            var fy = await fySvc.GetActiveAsync();
            if (fy is null) { _todoCount = 0; return; }

            var unmatched = await bankSvc.CountUnmatchedAsync(fy.Id);
            var unpaid = await invSvc.CountUnpaidAsync(fy.Id);
            _todoCount = unmatched + unpaid;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load to-do count for nav badge");
            _todoCount = 0;
        }
        finally
        {
            _loadingTodoCount = false;
        }
    }

    private async Task LoadDraftCountAsync()
    {
        if (_loadingDraftCount) return;
        _loadingDraftCount = true;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var fySvc = scope.ServiceProvider.GetRequiredService<IFiscalYearService>();
            var journalSvc = scope.ServiceProvider.GetRequiredService<IJournalEntryService>();

            var fy = await fySvc.GetActiveAsync();
            if (fy is null) { _draftCount = 0; return; }

            _draftCount = await journalSvc.CountDraftsAsync(fy.Id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load draft count for nav badge");
            _draftCount = 0;
        }
        finally
        {
            _loadingDraftCount = false;
        }
    }
```

with:

```csharp
    private async Task LoadTodoCountAsync()
    {
        if (_loadingTodoCount) return;
        _loadingTodoCount = true;
        try
        {
            // Use a dedicated scope so this background query doesn't share a DbContext
            // with the page's OnInitializedAsync, which would cause concurrent-operation exceptions.
            await using var scope = ScopeFactory.CreateAsyncScope();
            var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            var bankSvc = scope.ServiceProvider.GetRequiredService<IBankImportService>();
            var invSvc = scope.ServiceProvider.GetRequiredService<ISupplierInvoiceService>();

            if (currentUser.OrganisationId is not { } organisationId) { _todoCount = 0; return; }

            var unmatched = await bankSvc.CountUnmatchedForOrganisationAsync(organisationId);
            var unpaid = await invSvc.CountUnpaidForOrganisationAsync(organisationId);
            _todoCount = unmatched + unpaid;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load to-do count for nav badge");
            _todoCount = 0;
        }
        finally
        {
            _loadingTodoCount = false;
        }
    }

    private async Task LoadDraftCountAsync()
    {
        if (_loadingDraftCount) return;
        _loadingDraftCount = true;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            var journalSvc = scope.ServiceProvider.GetRequiredService<IJournalEntryService>();

            if (currentUser.OrganisationId is not { } organisationId) { _draftCount = 0; return; }

            _draftCount = await journalSvc.CountDraftsForOrganisationAsync(organisationId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load draft count for nav badge");
            _draftCount = 0;
        }
        finally
        {
            _loadingDraftCount = false;
        }
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors. (`ICurrentUser` is already `@using KoalaBooks.Domain.Interfaces` at the top of `MainLayout.razor:4`.)

- [ ] **Step 3: Manually verify**

Run the app, create two open fiscal years for the same org with an unpaid invoice in each. Confirm the "Att göra" nav badge counts both (previously it would only have counted the later year's).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Layout/MainLayout.razor
git commit -m "MainLayout: nav badges count organisation-wide, matching Todo/Review scope (#283)"
```

---

### Task 7: `Review.razor` — organisation-wide drafts + fiscal-year filter

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Review.razor` (full rewrite of the `@code` block and the fiscal-year gate)

**Interfaces:**
- Consumes: `IJournalEntryService.GetDraftsForOrganisationAsync` (Task 2), `IFiscalYearService.GetOpenFiscalYearsAsync` (Task 1), `ICurrentUser.OrganisationId`.

- [ ] **Step 1: Rewrite `Review.razor`**

Replace the entire file `src/KoalaBooks.Components/Pages/Review.razor` with:

```razor
@page "/review"
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Entities
@using KoalaBooks.Domain.Interfaces
@inject IJournalEntryService JournalEntryService
@inject IFiscalYearService FiscalYearService
@inject IAccountService AccountService
@inject ICurrentUser CurrentUser

<PageTitle>Att granska — KoalaBooks</PageTitle>

<h1>🔍 Att granska</h1>

@if (_isLoading)
{
    <MudProgressLinear Color="Color.Primary" Indeterminate="true" Class="mb-4" />
}
else
{
    <div class="toolbar" style="margin-bottom:1rem;">
        <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
        <select @bind="_selectedFiscalYearId" @bind:after="OnFilterChangedAsync" style="width:220px;">
            <option value="0">Alla räkenskapsår</option>
            @foreach (var fy in _openFiscalYears)
            {
                <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
            }
        </select>
    </div>

    <JournalReviewSection Entries="FilteredDrafts" Accounts="_accounts" OnEntriesChanged="ReloadDraftsAsync" />
}

@code {
    private List<FiscalYear> _openFiscalYears = [];
    private List<Account> _accounts = [];
    private List<JournalEntry> _drafts = [];
    private int _selectedFiscalYearId; // 0 = "Alla räkenskapsår"
    private bool _isLoading;

    private IEnumerable<JournalEntry> FilteredDrafts => _selectedFiscalYearId == 0
        ? _drafts
        : _drafts.Where(d => d.FiscalYearId == _selectedFiscalYearId);

    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        var accountsByFiscalYear = new List<Account>();
        foreach (var fy in _openFiscalYears)
            accountsByFiscalYear.AddRange(await AccountService.GetAllAsync(fy.Id));
        _accounts = accountsByFiscalYear.Where(a => a.IsActive).DistinctBy(a => a.AccountNumber).ToList();

        await ReloadDraftsAsync();
        _isLoading = false;
    }

    private async Task OnFilterChangedAsync() => await ReloadDraftsAsync();

    private async Task ReloadDraftsAsync()
    {
        var organisationId = CurrentUser.OrganisationId ?? throw new InvalidOperationException("No active tenant.");
        _drafts = await JournalEntryService.GetDraftsForOrganisationAsync(organisationId);
    }
}
```

Note: the `_accounts` list is built by unioning accounts across all open fiscal years (deduplicated by account number) because `JournalReviewSection` needs an account lookup for whichever draft the user is looking at, and drafts can now come from any open year, not just one.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Check for existing component tests covering Review.razor**

Run: `grep -rl "Render<Review>" tests/`

If a `ReviewTests.cs` exists, update its `IFiscalYearService`/`IJournalEntryService` stubs from `GetActiveAsync`/`GetByFiscalYearAsync` to `GetOpenFiscalYearsAsync`/`GetDraftsForOrganisationAsync`, following the same before/after pattern as Task 5, Steps 1-2, adapted to whatever assertions that file makes (read it fully before editing — do not guess its content).

- [ ] **Step 4: Manually verify**

Run the app. Create two open fiscal years, add a draft journal entry in each. Visit `/review` — confirm both drafts show under "Alla räkenskapsår", and selecting one specific year in the new dropdown filters to just that year's draft. Confirm the "Att granska" nav badge count (Task 6) matches the unfiltered count shown on this page.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Review.razor
git commit -m "Review.razor: organisation-wide drafts with an in-page fiscal year filter (#283)"
```

---

### Task 8: `Todo.razor` — organisation-wide unmatched/unpaid + fiscal-year filter

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Todo.razor` (fiscal-year gate, `OnInitializedAsync`, `ReloadItems`, and the bank-tx/invoice posting handlers that currently assume a single `_fiscalYear`)

**Interfaces:**
- Consumes: `IBankImportService.GetUnmatchedForOrganisationAsync`, `ISupplierInvoiceService.GetAllForOrganisationAsync` (Task 2), `IFiscalYearService.GetOpenFiscalYearsAsync` (Task 1), `ICurrentUser.OrganisationId`.

**Rationale:** Each `TodoItem` now needs to carry which fiscal year it belongs to (for `PostBankTxAsync`, which creates a `JournalEntry` and needs a concrete `FiscalYearId`, and for the account lookups, which are per-fiscal-year). Add `FiscalYearId` to the `TodoItem` record and look up per-item accounts instead of one page-wide `_fiscalYear`.

- [ ] **Step 1: Update the `@code` block**

In `src/KoalaBooks.Components/Pages/Todo.razor`, replace `@if (_fiscalYear is null)` at line 16 with a check against whether any open fiscal year exists:

```razor
else if (!_openFiscalYears.Any())
```

Add a fiscal-year filter toolbar row right after the opening `<div class="card">` at line 31 — replace:

```razor
    <div class="card">
        <p style="color:#64748b; margin:0 0 1rem 0; font-size:0.875rem;">@_items.Count poster att hantera</p>
```

with:

```razor
    <div class="toolbar" style="margin-bottom:1rem;">
        <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
        <select @bind="_selectedFiscalYearId" @bind:after="OnFilterChangedAsync" style="width:220px;">
            <option value="0">Alla räkenskapsår</option>
            @foreach (var fy in _openFiscalYears)
            {
                <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
            }
        </select>
    </div>

    <div class="card">
        <p style="color:#64748b; margin:0 0 1rem 0; font-size:0.875rem;">@FilteredItems.Count() poster att hantera</p>
```

Replace `@foreach (var item in _items)` at line 44 with `@foreach (var item in FilteredItems)`.

Replace the injects and fields (lines 126-161):

```csharp
    [Inject] private IFiscalYearService FiscalYearService { get; set; } = default!;
    [Inject] private IBankImportService BankImportService { get; set; } = default!;
    [Inject] private ISupplierInvoiceService SupplierInvoiceService { get; set; } = default!;
    [Inject] private IJournalEntryService JournalEntryService { get; set; } = default!;
    [Inject] private IAccountService AccountService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private FiscalYear? _fiscalYear;
    private List<TodoItem> _items = [];
    private List<Account> _allAccounts = [];
    private List<Account> _bankAccounts = [];
    private bool _isLoading;
    private string? _actionError;

    // Expand state
    private int? _expandedId;
    private TodoKind _expandedKind;
    private bool _actioning;

    // Bank tx form
    private int _contraAccountId;
    private bool _contraWasSuggested;
    private string _entryDescription = "";

    // Invoice payment form
    private DateTime _payDate = DateTime.Today;
    private int _payBankAccountId;
    private int _payPayableAccountId;

    private enum TodoKind { BankTx, Invoice }

    private record TodoItem(
        DateOnly Date, TodoKind Kind, string Description, decimal Amount,
        bool IsOverdue, int EntityId,
        string? AccountNumber = null, int? BankAccountId = null);
```

with:

```csharp
    [Inject] private IFiscalYearService FiscalYearService { get; set; } = default!;
    [Inject] private IBankImportService BankImportService { get; set; } = default!;
    [Inject] private ISupplierInvoiceService SupplierInvoiceService { get; set; } = default!;
    [Inject] private IJournalEntryService JournalEntryService { get; set; } = default!;
    [Inject] private IAccountService AccountService { get; set; } = default!;
    [Inject] private ICurrentUser CurrentUser { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private List<FiscalYear> _openFiscalYears = [];
    private List<TodoItem> _items = [];
    private List<Account> _allAccounts = [];
    private List<Account> _bankAccounts = [];
    private int _selectedFiscalYearId; // 0 = "Alla räkenskapsår"
    private bool _isLoading;
    private string? _actionError;

    // Expand state
    private int? _expandedId;
    private TodoKind _expandedKind;
    private bool _actioning;

    // Bank tx form
    private int _contraAccountId;
    private bool _contraWasSuggested;
    private string _entryDescription = "";

    // Invoice payment form
    private DateTime _payDate = DateTime.Today;
    private int _payBankAccountId;
    private int _payPayableAccountId;

    private enum TodoKind { BankTx, Invoice }

    private record TodoItem(
        DateOnly Date, TodoKind Kind, string Description, decimal Amount,
        bool IsOverdue, int EntityId, int FiscalYearId,
        string? AccountNumber = null, int? BankAccountId = null);

    private IEnumerable<TodoItem> FilteredItems => _selectedFiscalYearId == 0
        ? _items
        : _items.Where(i => i.FiscalYearId == _selectedFiscalYearId);
```

Replace `OnInitializedAsync` and add `OnFilterChangedAsync` (lines 163-175):

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _fiscalYear = await FiscalYearService.GetActiveAsync();
        if (_fiscalYear is not null)
        {
            _allAccounts = await AccountService.GetAllAsync(_fiscalYear.Id);
            _bankAccounts = _allAccounts.Where(a => a.AccountNumber.StartsWith("19")).ToList();

            await ReloadItems();
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        var accountsByFiscalYear = new List<Account>();
        foreach (var fy in _openFiscalYears)
            accountsByFiscalYear.AddRange(await AccountService.GetAllAsync(fy.Id));
        _allAccounts = accountsByFiscalYear.DistinctBy(a => a.AccountNumber).ToList();
        _bankAccounts = _allAccounts.Where(a => a.AccountNumber.StartsWith("19")).ToList();

        await ReloadItems();
        _isLoading = false;
    }

    private async Task OnFilterChangedAsync() => await ReloadItems();
```

Replace `ReloadItems` (lines 177-195):

```csharp
    private async Task ReloadItems()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        var txItems = (await BankImportService.GetUnmatchedAsync(_fiscalYear!.Id))
            .Select(tx => new TodoItem(
                tx.Date, TodoKind.BankTx, tx.Description, tx.Amount,
                false, tx.Id, tx.Account.AccountNumber, tx.AccountId));

        var invoices = await SupplierInvoiceService.GetAllAsync(_fiscalYear.Id);
        var invItems = invoices
            .Where(i => !i.IsPaid)
            .Select(i => new TodoItem(
                i.DueDate, TodoKind.Invoice,
                i.SupplierName + (i.InvoiceNumber is not null ? $" #{i.InvoiceNumber}" : ""),
                -i.TotalAmount, i.DueDate < today, i.Id));

        _items = txItems.Concat(invItems).OrderBy(i => i.Date).ThenBy(i => i.Kind).ToList();
    }
```

with:

```csharp
    private async Task ReloadItems()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var organisationId = CurrentUser.OrganisationId ?? throw new InvalidOperationException("No active tenant.");

        var txItems = (await BankImportService.GetUnmatchedForOrganisationAsync(organisationId))
            .Select(tx => new TodoItem(
                tx.Date, TodoKind.BankTx, tx.Description, tx.Amount,
                false, tx.Id, tx.Account.FiscalYearId, tx.Account.AccountNumber, tx.AccountId));

        var invoices = await SupplierInvoiceService.GetAllForOrganisationAsync(organisationId);
        var invItems = invoices
            .Where(i => !i.IsPaid)
            .Select(i => new TodoItem(
                i.DueDate, TodoKind.Invoice,
                i.SupplierName + (i.InvoiceNumber is not null ? $" #{i.InvoiceNumber}" : ""),
                -i.TotalAmount, i.DueDate < today, i.Id, i.FiscalYearId));

        _items = txItems.Concat(invItems).OrderBy(i => i.Date).ThenBy(i => i.Kind).ToList();
    }
```

Update `PostBankTxAsync` — replace `FiscalYearId = _fiscalYear!.Id,` (line 243) with `FiscalYearId = item.FiscalYearId,` since the fiscal year is now per-item, not page-wide:

```csharp
            var entry = new JournalEntry
            {
                Date = item.Date,
                Description = _entryDescription.Trim(),
                FiscalYearId = item.FiscalYearId,
```

Add `@using KoalaBooks.Domain.Interfaces` to the top of the file (after the existing `@using KoalaBooks.Domain.Enums` at line 4).

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Check for existing component tests covering Todo.razor**

Run: `grep -rl "Render<Todo>" tests/`

If found, read the file fully and update its stubs from `GetActiveAsync`/`GetUnmatchedAsync`/`GetAllAsync(fiscalYearId)` to `GetOpenFiscalYearsAsync`/`GetUnmatchedForOrganisationAsync`/`GetAllForOrganisationAsync`, matching whatever fixture data it sets up.

- [ ] **Step 4: Manually verify**

Run the app. Create two open fiscal years, add an unpaid supplier invoice in each. Visit `/todo` — confirm both show under "Alla räkenskapsår" and the year filter narrows correctly. Post one of the bank-tx items and confirm the created journal entry lands in the correct (per-item) fiscal year, not always the same one. Confirm the "Att göra" badge count matches the unfiltered page total.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Todo.razor
git commit -m "Todo.razor: organisation-wide unmatched/unpaid items with an in-page fiscal year filter (#283)"
```

---

### Task 9: `Inbox.razor` — fiscal-year filter (All / specific year / Undated)

**Files:**
- Modify: `src/KoalaBooks.Application/Services/IDocumentService.cs:10-16`, `DocumentService.cs:161-197`
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`

**Interfaces:**
- Produces (extends existing signatures, additive optional parameters — no existing caller breaks):
  ```csharp
  Task<List<DocumentMeta>> GetPendingAsync(
      string? typeFilter = null, int skip = 0, int? take = null,
      string sortBy = "uploadedAt", bool sortAsc = false,
      DateOnly? fiscalYearStart = null, DateOnly? fiscalYearEnd = null, bool undatedOnly = false);
  Task<int> GetPendingCountAsync(
      string? typeFilter = null,
      DateOnly? fiscalYearStart = null, DateOnly? fiscalYearEnd = null, bool undatedOnly = false);
  ```

**Rationale:** `Document` has no `FiscalYearId` — only a nullable `DocumentDate`. Filtering by fiscal year means matching `DocumentDate` against the selected year's `[StartDate, EndDate]`. Documents with `DocumentDate == null` can't be bucketed into any year, so they need their own explicit "Undated" filter value rather than silently vanishing from every specific-year filter or silently always appearing under "All" with no way to isolate them.

- [ ] **Step 1: Extend `IDocumentService`/`DocumentService`**

In `src/KoalaBooks.Application/Services/IDocumentService.cs`, replace lines 10-16:

```csharp
    Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false);
    Task<int> GetPendingCountAsync(string? typeFilter = null);
```

with:

```csharp
    Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false);
    Task<int> GetPendingCountAsync(
        string? typeFilter = null,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false);
```

In `src/KoalaBooks.Application/Services/DocumentService.cs`, replace lines 161-197:

```csharp
    public Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false)
    {
        var base2 = PendingQuery(typeFilter);
        IQueryable<Document> ordered = (sortBy, sortAsc) switch
        {
            ("fileName",     true)  => base2.OrderBy(d => d.FileName),
            ("fileName",     false) => base2.OrderByDescending(d => d.FileName),
            ("documentDate", true)  => base2.OrderBy(d => d.DocumentDate),
            ("documentDate", false) => base2.OrderByDescending(d => d.DocumentDate),
            (_,              true)  => base2.OrderBy(d => d.UploadedAt),
            _                       => base2.OrderByDescending(d => d.UploadedAt),
        };
        var q = ordered.Skip(skip);
        if (take.HasValue) q = q.Take(take.Value);
        return SelectMetaAsync(q);
    }

    public Task<int> GetPendingCountAsync(string? typeFilter = null) =>
        PendingQuery(typeFilter).CountAsync();

    private IQueryable<Document> PendingQuery(string? typeFilter)
    {
        var query = db.Documents
            .Where(d => !d.JournalEntries.Any() && !d.SupplierInvoices.Any() && !d.CustomerInvoices.Any());

        return typeFilter switch
        {
            "unclassified" => query.Where(d => d.ClassifiedType == null),
            null or "all"  => query,
            var t          => query.Where(d => d.ClassifiedType == t)
        };
    }
```

with:

```csharp
    public Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false)
    {
        var base2 = PendingQuery(typeFilter, fiscalYearStart, fiscalYearEnd, undatedOnly);
        IQueryable<Document> ordered = (sortBy, sortAsc) switch
        {
            ("fileName",     true)  => base2.OrderBy(d => d.FileName),
            ("fileName",     false) => base2.OrderByDescending(d => d.FileName),
            ("documentDate", true)  => base2.OrderBy(d => d.DocumentDate),
            ("documentDate", false) => base2.OrderByDescending(d => d.DocumentDate),
            (_,              true)  => base2.OrderBy(d => d.UploadedAt),
            _                       => base2.OrderByDescending(d => d.UploadedAt),
        };
        var q = ordered.Skip(skip);
        if (take.HasValue) q = q.Take(take.Value);
        return SelectMetaAsync(q);
    }

    public Task<int> GetPendingCountAsync(
        string? typeFilter = null,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false) =>
        PendingQuery(typeFilter, fiscalYearStart, fiscalYearEnd, undatedOnly).CountAsync();

    private IQueryable<Document> PendingQuery(
        string? typeFilter,
        DateOnly? fiscalYearStart = null,
        DateOnly? fiscalYearEnd = null,
        bool undatedOnly = false)
    {
        var query = db.Documents
            .Where(d => !d.JournalEntries.Any() && !d.SupplierInvoices.Any() && !d.CustomerInvoices.Any());

        query = typeFilter switch
        {
            "unclassified" => query.Where(d => d.ClassifiedType == null),
            null or "all"  => query,
            var t          => query.Where(d => d.ClassifiedType == t)
        };

        if (undatedOnly)
            return query.Where(d => d.DocumentDate == null);

        if (fiscalYearStart.HasValue && fiscalYearEnd.HasValue)
            return query.Where(d => d.DocumentDate >= fiscalYearStart && d.DocumentDate <= fiscalYearEnd);

        return query;
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors (existing callers of `GetPendingAsync`/`GetPendingCountAsync` compile unchanged since the new parameters are optional).

- [ ] **Step 3: Add the fiscal-year filter UI to `Inbox.razor`**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, add `@inject IFiscalYearService FiscalYearService` after line 6 (`@using Microsoft.AspNetCore.Components.Forms`), and add `@using KoalaBooks.Application.Services` if not already present (it is, line 3).

Add a second filter row after the existing type-filter row (after line 41, `</div>`):

```razor
<div style="display:flex; gap:0.25rem; margin-bottom:1rem;">
    <button class="btn btn-sm @(_fyFilter == "all" ? "btn-primary" : "btn-secondary")"
            @onclick='() => SetFyFilterAsync("all")'>Alla år</button>
    @foreach (var fy in _openFiscalYears)
    {
        <button class="btn btn-sm @(_fyFilter == fy.Id.ToString() ? "btn-primary" : "btn-secondary")"
                @onclick='() => SetFyFilterAsync(fy.Id.ToString())'>@fy.Name</button>
    }
    <button class="btn btn-sm @(_fyFilter == "undated" ? "btn-primary" : "btn-secondary")"
            @onclick='() => SetFyFilterAsync("undated")'>Odaterade</button>
</div>
```

Add `_openFiscalYears` field and load it in `OnInitializedAsync`, and add `_fyFilter` state + resolution helpers. Replace lines 120-169:

```csharp
@code {
    [Inject] private IDocumentService DocumentService { get; set; } = default!;
    [Inject] private IDocumentProvider DocumentProvider { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;

    private List<DocumentMeta> _docs = [];
    private bool _isLoading;
    private bool _uploading;
    private string? _error;
    private string _filter = "all";
    private string _sortBy = "uploadedAt";
    private bool _sortAsc = false;
    private int _page = 1;
    private int _totalCount;
    private const int PageSize = 50;
    private System.Threading.Timer? _pollTimer;
    private int _isPolling; // 0/1 guard, read/written across the timer thread and the
                             // dispatcher thread — needs Interlocked, not a plain bool.
    private bool _disposed;

    // A doc pending this long has exhausted its storage-load retries without the job
    // ever syncing that failure back to ExtractionStatus — stop polling for it.
    private static readonly TimeSpan PendingStaleAfter = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg" };

    private int TotalPages => (_totalCount + PageSize - 1) / PageSize;

    private static (string Label, string Value)[] Filters =>
    [
        ("Alla", "all"),
        ("Oklassificerade", "unclassified"),
        ("Leverantörsfaktura", nameof(DocumentEntityType.SupplierInvoice)),
        ("Kundfaktura", nameof(DocumentEntityType.CustomerInvoice)),
        ("Verifikation", nameof(DocumentEntityType.JournalEntry)),
    ];

    protected override async Task OnInitializedAsync() => await LoadPageAsync();

    private async Task LoadPageAsync(bool showSpinner = true)
    {
        if (showSpinner) _isLoading = true;
        var skip = (_page - 1) * PageSize;
        _docs = await DocumentService.GetPendingAsync(_filter, skip, PageSize, _sortBy, _sortAsc);
        _totalCount = await DocumentService.GetPendingCountAsync(_filter);
        _isLoading = false;
        UpdatePolling();
    }
```

with:

```csharp
@code {
    [Inject] private IDocumentService DocumentService { get; set; } = default!;
    [Inject] private IDocumentProvider DocumentProvider { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;

    private List<DocumentMeta> _docs = [];
    private List<KoalaBooks.Domain.Entities.FiscalYear> _openFiscalYears = [];
    private bool _isLoading;
    private bool _uploading;
    private string? _error;
    private string _filter = "all";
    private string _fyFilter = "all"; // "all" | "undated" | a FiscalYear.Id as string
    private string _sortBy = "uploadedAt";
    private bool _sortAsc = false;
    private int _page = 1;
    private int _totalCount;
    private const int PageSize = 50;
    private System.Threading.Timer? _pollTimer;
    private int _isPolling; // 0/1 guard, read/written across the timer thread and the
                             // dispatcher thread — needs Interlocked, not a plain bool.
    private bool _disposed;

    // A doc pending this long has exhausted its storage-load retries without the job
    // ever syncing that failure back to ExtractionStatus — stop polling for it.
    private static readonly TimeSpan PendingStaleAfter = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg" };

    private int TotalPages => (_totalCount + PageSize - 1) / PageSize;

    private static (string Label, string Value)[] Filters =>
    [
        ("Alla", "all"),
        ("Oklassificerade", "unclassified"),
        ("Leverantörsfaktura", nameof(DocumentEntityType.SupplierInvoice)),
        ("Kundfaktura", nameof(DocumentEntityType.CustomerInvoice)),
        ("Verifikation", nameof(DocumentEntityType.JournalEntry)),
    ];

    protected override async Task OnInitializedAsync()
    {
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();
        await LoadPageAsync();
    }

    private (DateOnly? Start, DateOnly? End, bool Undated) ResolveFyFilter()
    {
        if (_fyFilter == "undated") return (null, null, true);
        if (_fyFilter != "all" && int.TryParse(_fyFilter, out var fyId))
        {
            var fy = _openFiscalYears.FirstOrDefault(f => f.Id == fyId);
            if (fy is not null) return (fy.StartDate, fy.EndDate, false);
        }
        return (null, null, false);
    }

    private async Task SetFyFilterAsync(string fyFilter)
    {
        _fyFilter = fyFilter;
        _page = 1;
        await LoadPageAsync();
    }

    private async Task LoadPageAsync(bool showSpinner = true)
    {
        if (showSpinner) _isLoading = true;
        var skip = (_page - 1) * PageSize;
        var (fyStart, fyEnd, undated) = ResolveFyFilter();
        _docs = await DocumentService.GetPendingAsync(_filter, skip, PageSize, _sortBy, _sortAsc, fyStart, fyEnd, undated);
        _totalCount = await DocumentService.GetPendingCountAsync(_filter, fyStart, fyEnd, undated);
        _isLoading = false;
        UpdatePolling();
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Add a service-level test for the new filter logic**

Check for an existing `DocumentServiceTests.cs` (confirmed present: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`). Read it fully to find its `TestFixture`/seed conventions, then append:

```csharp
    [Fact]
    public async Task GetPendingAsync_FiscalYearRange_FiltersByDocumentDate()
    {
        var svc = new DocumentService(_f.Db, Substitute.For<IDocumentStorage>(), Substitute.For<IDocumentExtractionQueue>(), TestFixture.MakeTenant(_f.OrganisationId));
        _f.Db.Documents.AddRange(
            new Document { OrganisationId = _f.OrganisationId, FileName = "in-range.pdf", StorageKey = "k1", DocumentDate = new DateOnly(2026, 6, 1) },
            new Document { OrganisationId = _f.OrganisationId, FileName = "out-of-range.pdf", StorageKey = "k2", DocumentDate = new DateOnly(2027, 6, 1) },
            new Document { OrganisationId = _f.OrganisationId, FileName = "undated.pdf", StorageKey = "k3", DocumentDate = null });
        await _f.Db.SaveChangesAsync();

        var inRange = await svc.GetPendingAsync(fiscalYearStart: new DateOnly(2026, 1, 1), fiscalYearEnd: new DateOnly(2026, 12, 31));
        var undated = await svc.GetPendingAsync(undatedOnly: true);

        Assert.Single(inRange);
        Assert.Equal("in-range.pdf", inRange[0].FileName);
        Assert.Single(undated);
        Assert.Equal("undated.pdf", undated[0].FileName);
    }
```

Adjust the `Document` construction fields to match whatever `DocumentServiceTests.cs` already uses elsewhere in the file (e.g. it may already have a helper for seeding documents — prefer that helper over constructing `Document` inline if one exists).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~GetPendingAsync_FiscalYearRange_FiltersByDocumentDate"`
Expected: PASS.

- [ ] **Step 5: Manually verify**

Run the app, upload documents with different (or missing) document dates spanning two fiscal years, visit `/inbox`, and confirm the new fiscal-year filter row narrows the list correctly, including "Odaterade" isolating the undated ones.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/IDocumentService.cs src/KoalaBooks.Application/Services/DocumentService.cs src/KoalaBooks.Components/Pages/Inbox.razor tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "Inbox.razor: add fiscal year filter (all/specific year/undated) (#283)"
```

---

### Task 10: `ClassifyDocumentDialog.razor` — resolve fiscal year from the document's date

**Files:**
- Modify: `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor:249-280`
- Modify: `tests/KoalaBooks.ComponentTests/PreviewDocumentDialogTests.cs:57-58`

**Interfaces:**
- Consumes: `IFiscalYearService.GetForDateAsync(DateOnly)` (Task 1).

**Rationale:** The document being classified already carries the date (`DocumentMeta.ResolvePrefillDate(Doc.DocumentDate, ex?.InvoiceDate)`) that determines which fiscal year it belongs to. Using "today" or a page-wide default here is a second, independent source of mis-resolution distinct from the badge/dashboard cases — a document dated in a just-closed or not-yet-current fiscal year should classify into *that* year, not whichever one happens to be "active" right now.

- [ ] **Step 1: Update the component test stub first**

In `tests/KoalaBooks.ComponentTests/PreviewDocumentDialogTests.cs`, replace lines 57-58:

```csharp
        var fiscalYearService = Substitute.For<IFiscalYearService>();
        fiscalYearService.GetActiveAsync().Returns((FiscalYear?)null);
```

with:

```csharp
        var fiscalYearService = Substitute.For<IFiscalYearService>();
        fiscalYearService.GetForDateAsync(Arg.Any<DateOnly>()).Returns((FiscalYear?)null);
```

(`MakeDoc()` in that file sets `DocumentDate = new DateOnly(2026, 1, 10)`, so a more precise stub — `GetForDateAsync(new DateOnly(2026, 1, 10))` — would also work; `Arg.Any<DateOnly>()` is used here because the dialog resolves the prefill date via `DocumentMeta.ResolvePrefillDate`, which this test doesn't otherwise exercise.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~PreviewDocumentDialogTests"`
Expected: FAIL — `ClassifyDocumentDialog` still calls `GetActiveAsync()`, unstubbed now, returning `null` by NSubstitute default (same as before, so this may actually still pass by coincidence for the null case — but proceed to Step 3 regardless, since the goal is behavioral correctness, not just making this one assertion pass).

- [ ] **Step 3: Update `ClassifyDocumentDialog.razor`**

In `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor`, replace `OnInitializedAsync` (lines 249-280):

```csharp
    protected override async Task OnInitializedAsync()
    {
        _type = Doc.ClassifiedType ?? Doc.SuggestedType ?? "";
        _fiscalYear = await FiscalYearService.GetActiveAsync();

        ExtractionResult? ex = null;
        if (Doc.ExtractedDataJson is not null)
        {
            try { ex = JsonSerializer.Deserialize<ExtractionResult>(Doc.ExtractedDataJson); }
            catch { }
        }

        if (ex is not null)
        {
            _supplier = ex.Supplier ?? "";
            _invoiceNumber = ex.InvoiceNumber ?? "";
            _amountExcl = ex.Amount ?? 0;
            _vatAmount = ex.VatAmount ?? 0;
            if (ex.DueDate.HasValue) _dueDate = ex.DueDate.Value.ToDateTime(TimeOnly.MinValue);
        }

        // Prefer the persisted (possibly user-edited) Bokföringsdatum from the inbox
        // preview over the raw AI-extracted invoice date.
        var prefill = DocumentMeta.ResolvePrefillDate(Doc.DocumentDate, ex?.InvoiceDate);
        if (prefill.HasValue) _date = prefill.Value;

        if (_fiscalYear is not null)
        {
            _accounts = await AccountService.GetAllAsync(_fiscalYear.Id);
            _customers = await CustomerService.GetAllAsync(_fiscalYear.OrganisationId);
        }
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _type = Doc.ClassifiedType ?? Doc.SuggestedType ?? "";

        ExtractionResult? ex = null;
        if (Doc.ExtractedDataJson is not null)
        {
            try { ex = JsonSerializer.Deserialize<ExtractionResult>(Doc.ExtractedDataJson); }
            catch { }
        }

        if (ex is not null)
        {
            _supplier = ex.Supplier ?? "";
            _invoiceNumber = ex.InvoiceNumber ?? "";
            _amountExcl = ex.Amount ?? 0;
            _vatAmount = ex.VatAmount ?? 0;
            if (ex.DueDate.HasValue) _dueDate = ex.DueDate.Value.ToDateTime(TimeOnly.MinValue);
        }

        // Prefer the persisted (possibly user-edited) Bokföringsdatum from the inbox
        // preview over the raw AI-extracted invoice date.
        var prefill = DocumentMeta.ResolvePrefillDate(Doc.DocumentDate, ex?.InvoiceDate);
        if (prefill.HasValue) _date = prefill.Value;

        // Resolve from the document's own date, not "today" or a page default — a document
        // dated into a specific fiscal year must classify into that year regardless of
        // which year is currently the default working year.
        _fiscalYear = await FiscalYearService.GetForDateAsync(DateOnly.FromDateTime(_date));

        if (_fiscalYear is not null)
        {
            _accounts = await AccountService.GetAllAsync(_fiscalYear.Id);
            _customers = await CustomerService.GetAllAsync(_fiscalYear.OrganisationId);
        }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~PreviewDocumentDialogTests"`
Expected: PASS.

- [ ] **Step 5: Manually verify**

Run the app with two fiscal years (2025 closed, 2026 open). Upload and classify a document dated in 2025 — confirm it resolves into the 2025 fiscal year's accounts, not 2026's.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor tests/KoalaBooks.ComponentTests/PreviewDocumentDialogTests.cs
git commit -m "ClassifyDocumentDialog: resolve fiscal year from the document's own date (#283)"
```

---

### Task 11: `SupplierInvoices.razor` — add fiscal-year selector wired to shared context

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/SupplierInvoices.razor` (toolbar markup near the top of the page, injects, `OnInitializedAsync`)

**Interfaces:**
- Consumes: `FiscalYearSelectionContext` (Task 3), `IFiscalYearService.GetOpenFiscalYearsAsync`/`GetDefaultFiscalYearAsync` (Task 1).

- [ ] **Step 1: Add the selector and wire the shared context**

Add `[Inject] private FiscalYearSelectionContext SelectionContext { get; set; } = default!;` alongside the page's other `[Inject]` fields (near the top of its `@code` block, immediately after the existing `[Inject] private IFiscalYearService FiscalYearService { get; set; } = default!;` — locate it with `grep -n "IFiscalYearService FiscalYearService" src/KoalaBooks.Components/Pages/SupplierInvoices.razor` since the exact line wasn't captured in this plan's file reads).

Add a fiscal-year selector row into the page's toolbar markup, immediately before the first existing `<div class="toolbar"` element:

```razor
<div class="toolbar" style="margin-bottom:1rem;">
    <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
    <select @bind="_fiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:220px;">
        @foreach (var fy in _openFiscalYears)
        {
            <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
        }
    </select>
</div>
```

Add a backing field `private List<FiscalYear> _openFiscalYears = [];` and `private int _fiscalYearId;` near the existing `private FiscalYear? _fiscalYear;` field.

Replace `OnInitializedAsync` (currently lines 458-470):

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _fiscalYear = await FiscalYearService.GetActiveAsync();
        if (_fiscalYear is not null)
        {
            _allAccounts = await AccountService.GetAllAsync(_fiscalYear.Id);
            _bankAccounts = _allAccounts.Where(a => a.AccountNumber.StartsWith("19")).ToList();
            await LoadInvoices();
            _knownSuppliers = await InvoiceService.GetSuppliersAsync(_fiscalYear.Id);
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }

    private async Task OnFiscalYearChangedAsync()
    {
        SelectionContext.Set(_fiscalYearId);
        await LoadForSelectedYearAsync();
    }

    private async Task LoadForSelectedYearAsync()
    {
        _fiscalYear = _openFiscalYears.FirstOrDefault(f => f.Id == _fiscalYearId)
            ?? await FiscalYearService.GetByIdAsync(_fiscalYearId);
        if (_fiscalYear is null) return;

        _allAccounts = await AccountService.GetAllAsync(_fiscalYear.Id);
        _bankAccounts = _allAccounts.Where(a => a.AccountNumber.StartsWith("19")).ToList();
        await LoadInvoices();
        _knownSuppliers = await InvoiceService.GetSuppliersAsync(_fiscalYear.Id);
    }
```

Note: `LoadForSelectedYearAsync` falls back to `GetByIdAsync` because the seeded/shared context year might be closed (not in `_openFiscalYears`) — a user should still be able to view a closed year's invoices read-only if that's what they were last looking at elsewhere; the dropdown itself only lists open years for new selections, matching the existing behavior where this page never worked with closed years before.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Manually verify**

Run the app with two open fiscal years. On `/reports/trial-balance`, select the older year (this writes to the shared context in Task 15). Navigate to `/supplier-invoices` — confirm it defaults to the same year rather than "today's" year. Change the selector here and confirm invoices reload for the new year.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/SupplierInvoices.razor
git commit -m "SupplierInvoices.razor: add fiscal year selector, seeded from shared selection context (#283)"
```

---

### Task 12: `BankImport.razor` — add fiscal-year selector wired to shared context

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/BankImport.razor`

**Interfaces:**
- Consumes: same as Task 11.

- [ ] **Step 1: Add the selector and wire the shared context**

Follow the identical pattern from Task 11: add `[Inject] private FiscalYearSelectionContext SelectionContext { get; set; } = default!;`, add a fiscal-year `<select>` bound to `_fiscalYearId` with `@bind:after="OnFiscalYearChangedAsync"`, add `_openFiscalYears`/`_fiscalYearId` fields.

Replace `OnInitializedAsync` (currently lines 462-477):

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _fiscalYear = await FiscalYearService.GetActiveAsync();
        if (_fiscalYear is not null)
        {
            _bankAccounts = await BankImportService.GetImportableAccountsAsync(_fiscalYear.Id, AccountPrefix);
            _allAccounts = await AccountService.GetAllAsync(_fiscalYear.Id);
            if (_bankAccounts.Any())
            {
                _selectedAccountId = AutoSelectAccount();
                await LoadTransactions();
            }
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }

    private async Task OnFiscalYearChangedAsync()
    {
        SelectionContext.Set(_fiscalYearId);
        await LoadForSelectedYearAsync();
    }

    private async Task LoadForSelectedYearAsync()
    {
        _fiscalYear = _openFiscalYears.FirstOrDefault(f => f.Id == _fiscalYearId)
            ?? await FiscalYearService.GetByIdAsync(_fiscalYearId);
        if (_fiscalYear is null) return;

        _bankAccounts = await BankImportService.GetImportableAccountsAsync(_fiscalYear.Id, AccountPrefix);
        _allAccounts = await AccountService.GetAllAsync(_fiscalYear.Id);
        if (_bankAccounts.Any())
        {
            _selectedAccountId = AutoSelectAccount();
            await LoadTransactions();
        }
    }
```

`OnParametersSetAsync` (currently lines 488-506, which re-initializes on navigation between `/import/bank` and `/import/tax`) keeps calling `BankImportService.GetImportableAccountsAsync(_fiscalYear.Id, AccountPrefix)` unchanged — it already guards on `_fiscalYear is not null`, so no change needed there.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Manually verify**

Same manual check as Task 11, applied to `/import/bank`.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/BankImport.razor
git commit -m "BankImport.razor: add fiscal year selector, seeded from shared selection context (#283)"
```

---

### Task 13: `CustomerInvoices.razor` — add fiscal-year selector wired to shared context

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`

**Interfaces:**
- Consumes: same as Task 11.

- [ ] **Step 1: Add the selector and wire the shared context**

Same pattern as Task 11. Replace `OnInitializedAsync` (currently lines 480-492):

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _fiscalYear = await FiscalYearService.GetActiveAsync();
        if (_fiscalYear is not null)
        {
            _allAccounts = await AccountService.GetAllAsync(_fiscalYear.Id);
            _bankAccounts = _allAccounts.Where(a => a.AccountNumber.StartsWith("19")).ToList();
            _customers = await CustomerSvc.GetAllAsync(_fiscalYear.OrganisationId);
            await LoadInvoices();
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }

    private async Task OnFiscalYearChangedAsync()
    {
        SelectionContext.Set(_fiscalYearId);
        await LoadForSelectedYearAsync();
    }

    private async Task LoadForSelectedYearAsync()
    {
        _fiscalYear = _openFiscalYears.FirstOrDefault(f => f.Id == _fiscalYearId)
            ?? await FiscalYearService.GetByIdAsync(_fiscalYearId);
        if (_fiscalYear is null) return;

        _allAccounts = await AccountService.GetAllAsync(_fiscalYear.Id);
        _bankAccounts = _allAccounts.Where(a => a.AccountNumber.StartsWith("19")).ToList();
        _customers = await CustomerSvc.GetAllAsync(_fiscalYear.OrganisationId);
        await LoadInvoices();
    }
```

Add the same selector `<select>` markup, `[Inject] FiscalYearSelectionContext`, and `_openFiscalYears`/`_fiscalYearId` fields as Task 11.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Manually verify**

Same manual check as Task 11, applied to `/customer-invoices`.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/CustomerInvoices.razor
git commit -m "CustomerInvoices.razor: add fiscal year selector, seeded from shared selection context (#283)"
```

---

### Task 14: `Accounts.razor` — add fiscal-year selector wired to shared context

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Accounts.razor:17-25,213,239-257`

**Interfaces:**
- Consumes: same as Task 11.

**Rationale:** Unlike the report pages, `Accounts.razor` has no `<select>` at all today — it only shows `_activeFiscalYear.Name` as static text, computed from the same ad-hoc "latest non-closed" heuristic as the report pages but with no way for the user to override it.

- [ ] **Step 1: Add the selector**

Replace the static text at line 25:

```razor
<p style="color:#64748b; margin-bottom:1rem;">Räkenskapsår: <strong>@_activeFiscalYear.Name</strong></p>
```

with:

```razor
<div class="toolbar" style="margin-bottom:1rem;">
    <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
    <select @bind="_fiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:220px;">
        @foreach (var fy in _openFiscalYears)
        {
            <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
        }
    </select>
</div>
```

Add `[Inject] private FiscalYearSelectionContext SelectionContext { get; set; } = default!;` next to the page's other injects, and `private List<FiscalYear> _openFiscalYears = [];`, `private int _fiscalYearId;` next to `private FiscalYear? _activeFiscalYear;` (line 213).

Replace `OnInitializedAsync` (lines 239-252):

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        var allYears = await FiscalYearService.GetAllAsync();
        _activeFiscalYear = allYears.FirstOrDefault(f => !f.IsClosed)
                         ?? allYears.OrderByDescending(f => f.StartDate).FirstOrDefault();
        _otherFiscalYears = allYears
            .Where(f => f.Id != _activeFiscalYear?.Id)
            .OrderByDescending(f => f.StartDate)
            .ToList();
        if (_activeFiscalYear is not null)
            await LoadAccounts();
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        var allYears = await FiscalYearService.GetAllAsync();
        _openFiscalYears = allYears.Where(f => !f.IsClosed).OrderByDescending(f => f.StartDate).ToList();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        _activeFiscalYear = seed;
        _otherFiscalYears = allYears
            .Where(f => f.Id != _activeFiscalYear?.Id)
            .OrderByDescending(f => f.StartDate)
            .ToList();
        if (_activeFiscalYear is not null)
        {
            _fiscalYearId = _activeFiscalYear.Id;
            await LoadAccounts();
        }
        _isLoading = false;
    }

    private async Task OnFiscalYearChangedAsync()
    {
        SelectionContext.Set(_fiscalYearId);
        _activeFiscalYear = _openFiscalYears.FirstOrDefault(f => f.Id == _fiscalYearId);
        _otherFiscalYears = (await FiscalYearService.GetAllAsync())
            .Where(f => f.Id != _fiscalYearId)
            .OrderByDescending(f => f.StartDate)
            .ToList();
        await LoadAccounts();
    }
```

`LoadAccounts()` already reads `_activeFiscalYear!.Id` (line 256) — unchanged, since `_activeFiscalYear` stays the single source of truth for the rest of the page's logic (account-copy, BAS import, etc. at lines 301/327/367/388), only how it's picked has changed.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Manually verify**

Run the app with two open fiscal years, visit `/accounts`, confirm the new selector defaults sensibly and switching it reloads the account list and the "copy accounts from source year" section (`_otherFiscalYears`) correctly excludes whichever year is currently selected.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Accounts.razor
git commit -m "Accounts.razor: add fiscal year selector (previously fixed to an ambiguous default) (#283)"
```

---

### Task 15: Report-page cluster — wire existing selectors to shared context

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/TrialBalance.razor:68-85`
- Modify: `src/KoalaBooks.Components/Pages/GeneralLedger.razor:192-209`
- Modify: `src/KoalaBooks.Components/Pages/VatReport.razor:170-188`
- Modify: `src/KoalaBooks.Components/Pages/IncomeStatement.razor:98-114`
- Modify: `src/KoalaBooks.Components/Pages/BalanceSheet.razor:106-120`
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:369-382`

**Interfaces:**
- Consumes: same as Task 11. These six pages already have a working `<select>` — this task only replaces the ad-hoc "latest non-closed" default and adds a write to `FiscalYearSelectionContext` on change; no new markup.

- [ ] **Step 1: `TrialBalance.razor`**

Add `[Inject] private FiscalYearSelectionContext SelectionContext { get; set; } = default!;` after the existing `[Inject] private IFiscalYearService FiscalYearService { get; set; } = default!;` (line 70).

Replace lines 76-90:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();
        var active = _fiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _fiscalYears.FirstOrDefault();
        if (active is not null)
        {
            SelectedFiscalYearId = active.Id;
            await LoadReport();
        }
    }

    private async Task LoadReport()
    {
        _rows = await JournalReportingService.GetTrialBalanceAsync(SelectedFiscalYearId);
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }

    private async Task LoadReport()
    {
        SelectionContext.Set(SelectedFiscalYearId);
        _rows = await JournalReportingService.GetTrialBalanceAsync(SelectedFiscalYearId);
    }
```

(`LoadReport` is already the `@bind:after` handler for the `<select>` at line 13, so writing to the context there covers both the initial load and every subsequent user change in one place.)

- [ ] **Step 2: `GeneralLedger.razor`**

Add the same `[Inject] FiscalYearSelectionContext` next to its existing `IFiscalYearService` inject.

Replace lines 192-209:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        try
        {
            _fiscalYears = await FiscalYearService.GetAllAsync();
            var active = _fiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _fiscalYears.FirstOrDefault();
            if (active is not null)
            {
                SelectedFiscalYearId = active.Id;
                await LoadAccountList();
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task LoadAccountList()
    {
        _expandedIds.Clear();
        _loadingIds.Clear();
        _loadedSections.Clear();
        _accounts = await AccountService.GetAllAsync(SelectedFiscalYearId);
        _computedBalances = await JournalReportingService.GetComputedBalancesAsync(SelectedFiscalYearId);
        _accountsWithTransactions = await JournalReportingService.GetAccountIdsWithTransactionsAsync(SelectedFiscalYearId, FromDate, ToDate, includeClosingEntries: true);
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        try
        {
            _fiscalYears = await FiscalYearService.GetAllAsync();

            FiscalYear? seed = null;
            if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
                seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
            seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

            if (seed is not null)
            {
                SelectedFiscalYearId = seed.Id;
                await LoadAccountList();
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task LoadAccountList()
    {
        SelectionContext.Set(SelectedFiscalYearId);
        _expandedIds.Clear();
        _loadingIds.Clear();
        _loadedSections.Clear();
        _accounts = await AccountService.GetAllAsync(SelectedFiscalYearId);
        _computedBalances = await JournalReportingService.GetComputedBalancesAsync(SelectedFiscalYearId);
        _accountsWithTransactions = await JournalReportingService.GetAccountIdsWithTransactionsAsync(SelectedFiscalYearId, FromDate, ToDate, includeClosingEntries: true);
    }
```

- [ ] **Step 3: `VatReport.razor`**

Add the same `[Inject] FiscalYearSelectionContext`.

Replace lines 170-179:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();
        var active = _fiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _fiscalYears.FirstOrDefault();
        if (active is not null)
        {
            SelectedFiscalYearId = active.Id;
            await LoadReport();
        }
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

This page's `<select @bind="SelectedFiscalYearId" @bind:after="OnFiscalYearChanged" ...>` (line 17) already routes through `OnFiscalYearChanged` (lines 181-188), which calls `LoadReport()`. Add the context write there instead of `LoadReport` (since `LoadReport` is also called by `SetQuarter`, which doesn't change the fiscal year):

Replace lines 181-188:

```csharp
    private async Task OnFiscalYearChanged()
    {
        _data = null;
        FromDate = null;
        ToDate = null;
        _quarterWarning = null;
        await LoadReport();
    }
```

with:

```csharp
    private async Task OnFiscalYearChanged()
    {
        SelectionContext.Set(SelectedFiscalYearId);
        _data = null;
        FromDate = null;
        ToDate = null;
        _quarterWarning = null;
        await LoadReport();
    }
```

- [ ] **Step 4: `IncomeStatement.razor`**

Add the same `[Inject] FiscalYearSelectionContext`.

Replace lines 98-107:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();
        var active = _fiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _fiscalYears.FirstOrDefault();
        if (active is not null)
        {
            SelectedFiscalYearId = active.Id;
            await LoadReport();
        }
    }

    private async Task LoadReport()
    {
        var result = await JournalReportingService.GetIncomeStatementAsync(SelectedFiscalYearId, FromDate, ToDate);
        _sections = result.Sections;
        _netResult = result.NetResult;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }

    private async Task LoadReport()
    {
        SelectionContext.Set(SelectedFiscalYearId);
        var result = await JournalReportingService.GetIncomeStatementAsync(SelectedFiscalYearId, FromDate, ToDate);
        _sections = result.Sections;
        _netResult = result.NetResult;
    }
```

(This page's `<select>` at line 13 has no `@bind:after` — the user changes the dropdown and clicks the existing "Generera" button, which calls `LoadReport()` directly, so writing the context there covers it; no markup change needed.)

- [ ] **Step 5: `BalanceSheet.razor`**

Add the same `[Inject] FiscalYearSelectionContext`.

Replace lines 106-120:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();
        var active = _fiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _fiscalYears.FirstOrDefault();
        if (active is not null)
        {
            SelectedFiscalYearId = active.Id;
            await LoadReport();
        }
    }

    private async Task LoadReport()
    {
        _sections = await JournalReportingService.GetBalanceSheetAsync(SelectedFiscalYearId);
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }

    private async Task LoadReport()
    {
        SelectionContext.Set(SelectedFiscalYearId);
        _sections = await JournalReportingService.GetBalanceSheetAsync(SelectedFiscalYearId);
    }
```

- [ ] **Step 6: `Journal.razor`**

Add the same `[Inject] FiscalYearSelectionContext`.

Replace lines 369-382:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _allFiscalYears = await FiscalYearService.GetAllAsync();
        _activeFiscalYear = _allFiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _allFiscalYears.FirstOrDefault();
        if (!_allFiscalYears.Any())
        {
            _isLoading = false;
            return;
        }
        _selectedFiscalYearId = _activeFiscalYear?.Id ?? _allFiscalYears.First().Id;
        await LoadForSelectedYearAsync();
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _allFiscalYears = await FiscalYearService.GetAllAsync();
        if (!_allFiscalYears.Any())
        {
            _isLoading = false;
            return;
        }

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _allFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        _activeFiscalYear = seed;
        _selectedFiscalYearId = _activeFiscalYear?.Id ?? _allFiscalYears.First().Id;
        await LoadForSelectedYearAsync();
        _isLoading = false;
    }
```

This page's `<select @bind="_selectedFiscalYearId" @bind:after="OnFiscalYearChangedAsync" ...>` (line 29) already routes through an `OnFiscalYearChangedAsync` handler. Find it (`grep -n "OnFiscalYearChangedAsync" src/KoalaBooks.Components/Pages/Journal.razor`) and add `SelectionContext.Set(_selectedFiscalYearId);` as its first line, before whatever reload logic it already runs — read the method fully before editing so the existing logic (e.g. resetting the month filter, checking `_isDirty`) isn't disturbed.

- [ ] **Step 7: Build**

Run: `dotnet build`
Expected: 0 errors, across all six pages.

- [ ] **Step 8: Manually verify context propagation across the whole cluster**

Run the app with two open fiscal years. On `/reports/trial-balance`, switch to the older year. Navigate through `/reports/general-ledger`, `/reports/vat`, `/reports/income-statement`, `/reports/balance-sheet`, `/journal`, `/supplier-invoices`, `/import/bank`, `/customer-invoices`, `/accounts` in sequence — confirm every one defaults to the same year you picked on TrialBalance, until you explicitly change it somewhere, at which point that new choice propagates to the next page you visit.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Components/Pages/TrialBalance.razor src/KoalaBooks.Components/Pages/GeneralLedger.razor \
        src/KoalaBooks.Components/Pages/VatReport.razor src/KoalaBooks.Components/Pages/IncomeStatement.razor \
        src/KoalaBooks.Components/Pages/BalanceSheet.razor src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "Report pages: seed fiscal year selector from shared context, replacing ad-hoc defaults (#283)"
```

---

### Task 16: Remove `GetActiveAsync` — final cleanup

**Files:**
- Modify: `src/KoalaBooks.Application/Services/IFiscalYearService.cs`
- Modify: `src/KoalaBooks.Application/Services/FiscalYearService.cs:33-39`
- Modify: `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs:57-70`

**Interfaces:**
- Removes: `Task<FiscalYear?> GetActiveAsync()` from `IFiscalYearService`/`FiscalYearService`.

- [ ] **Step 1: Confirm no remaining callers**

Run: `grep -rn "GetActiveAsync" src/`
Expected: no matches (Tasks 4-15 migrated every production caller).

If any match remains, stop and migrate it first — do not delete the method while a caller still depends on it.

- [ ] **Step 2: Remove the method from the interface**

In `src/KoalaBooks.Application/Services/IFiscalYearService.cs`, delete the line `Task<FiscalYear?> GetActiveAsync();`.

- [ ] **Step 3: Remove the implementation**

In `src/KoalaBooks.Application/Services/FiscalYearService.cs`, delete lines 33-39:

```csharp
    public async Task<FiscalYear?> GetActiveAsync()
    {
        return await _db.FiscalYears
            .Where(f => !f.IsClosed)
            .OrderByDescending(f => f.StartDate)
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }

```

- [ ] **Step 4: Replace the obsolete unit test**

In `tests/KoalaBooks.Tests/FiscalYearServiceTests.cs`, delete the `GetActiveAsync_ReturnsNonClosedYear` test (lines 57-70) — it's superseded by `GetForDateAsync`/`GetDefaultFiscalYearAsync` coverage added in Task 1.

- [ ] **Step 5: Build and run the full test suite**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test`
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/IFiscalYearService.cs src/KoalaBooks.Application/Services/FiscalYearService.cs tests/KoalaBooks.Tests/FiscalYearServiceTests.cs
git commit -m "Remove GetActiveAsync now that every caller resolves fiscal year explicitly (#283)"
```

---

## Non-goals (explicitly out of scope)

- `AccountMapping.razor` — never called `GetActiveAsync()`, already uses explicit source/target year pickers.
- **Correction (2026-07-19):** PR #278 is merged into `main` (confirmed via `gh pr view 278`), not an unmerged WASM-branch-only addition as originally stated here. `FiscalYearsController.GetActive()` and its three `{fiscalYearId}`-scoped count-endpoint siblings (`JournalEntriesController`, `BankTransactionsController`, `SupplierInvoicesController`) are real production callers on `main` today. They are still out of scope for the tasks written in this plan (no task above touches `KoalaBooks.Web/Controllers/Api`), but Task 16's "confirm no remaining callers" grep will find `FiscalYearsController.GetActive()` and block on it — a follow-up task is needed before Task 16 can run: rename `/active` to `/default` backed by `GetDefaultFiscalYearAsync()` per the ticket's own suggestion, and give the three count endpoints organisation-scoped counterparts backed by this plan's Task 2 methods.
