# Livingston — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Tester
- **Joined:** 2026-04-15T15:25:43.800Z

## Learnings

### 2026-04-15: P0 Bug Fix Test Suite

**Test patterns:**
- Each test class implements `IDisposable`, in-memory SQLite (`Data Source=:memory:`), xUnit Facts/Theories
- Setup: `DbContextOptionsBuilder<AppDbContext>().UseSqlite(...)` → `OpenConnection()` → `EnsureCreated()`
- Services instantiated directly with `AppDbContext` (no DI in unit tests)
- Naming: `ClassName_Condition_ExpectedResult` style
- All tests in root namespace `KoalaBooks.Tests`, no subdirectories

**Test files created (54 tests total):**
- `AccountClassMapperTests.cs` — P0 #1: BAS account class mapping (20xx=Equity, 21xx-29xx=Liability, 8xxx≠Equity)
- `BalanceFormulaTests.cs` — P0 #2: Credit-normal balance formulas (Liability/Equity/Revenue show positive balances)
- `DraftFilteringTests.cs` — P0 #3: Trial balance, balance sheet, income statement exclude draft entries
- `SieExportDiTests.cs` — P0 #4: SieExportService DI resolution
- `ReversalClosedYearTests.cs` — P0 #5: Reversals blocked in closed fiscal years
- `DateAccountValidationTests.cs` — P0 #7: Date range and account existence validation

**Key patterns for creating posted entries in tests:**
- `_service.CreateAsync(entry)` creates draft → `_service.PostAsync(id)` posts it
- For reports that filter by IsPosted, always post entries explicitly

**Key domain types:**
- `AccountClass` enum: Asset=1, Liability=2, Revenue=3, Expense=4, Equity=8
- `AccountClassMapper.FromAccountNumber()` — static, in Infrastructure
- `JournalEntryService` — all report methods (trial balance, balance sheet, income statement, general ledger)
- `TrialBalanceRow.Balance` — computed property, account-class-aware after P0 #2 fix

### 2026-04-17: Year-End Closing Test Suite

**File:** `YearEndClosingServiceTests.cs` — 19 tests written from Danny's ADR spec before service exists

**Tests cover three service methods:**
- `ValidateForClosingAsync` (4 tests): unposted drafts, already closed, valid year, year not found
- `PreviewClosingAsync` (4 tests): normal year, zero net result, IB-only accounts, invalid year
- `ExecuteClosingAsync` (9 tests): normal closing, UB computation, auto-create 8999/2099, posted+marked, zero result, IsClosed/ClosedAt, next-year propagation, dormant year, blocked by drafts
- Edge cases (2 tests): sequential entry numbers, dates match fiscal year end

**Expected result types (Linus to create):**
- `ClosingValidationResult` with `IsValid` (bool), `Errors` (list of strings)
- `ClosingPreview` with `IsValid`, `Errors`, `Entries` (list of JournalEntry), `NetResult` (decimal)
- `ClosingResult` with `IsSuccess` (bool), `Errors` (list of strings)

**Entity changes required (Linus Phase 1):**
- `JournalEntry.IsClosingEntry` (bool) — marks system-generated closing entries
- `FiscalYear.ClosedAt` (DateTime?) — timestamp of closing

**Key test setup pattern:**
- Base setup: fiscal year + 5 standard accounts (cash, liability, equity, revenue, expense)
- `AddResultAccounts()` helper adds 8999 + 2099 when needed
- `SetupNormalYear()` helper: revenue 10,000 + expense 6,000 → profit 4,000
- Tests verify database state directly (robust to return type changes)

### 2026-04-18: Test Infrastructure Refactor + Regression Tests Batch 2

**TestFixture shared helper (TestFixture.cs):**
- Eliminated duplicated SQLite in-memory DB setup across 13 test files
- Provides: `Db`, `JournalEntryService`, `FiscalYearService`, `YearEndClosingService`, `SieExportService`
- Seed helpers: `CreateFiscalYear()`, `CreateAccount()`, `MakeEntry()`, `CreateAndPostEntryAsync()`, `CreateStandardAccounts()`
- All 13 existing test classes refactored to use `_f = new TestFixture()` pattern
- Files NOT using TestFixture: `AccountClassMapperTests.cs` (static methods, no DB), `SieExportDiTests.cs` (tests DI container directly)

**New regression test files (10 tests total):**
- `ClosingEntryFilterTests.cs` — 3 tests: income statement excludes closing entries, balance sheet includes them, trial balance supports excludeClosingEntries parameter
- `SieExportDraftFilterTests.cs` — 2 tests: SIE export excludes draft entries, includes posted entries
- `ReversalDateClampingTests.cs` — 2 tests: reversal date clamped to FY end when today is after, uses today when within FY
- `PLBalancePropagationTests.cs` — 2 tests: P&L accounts get zero IB in new year, B/S accounts keep UB as IB
- `PostFiscalYearGuardTests.cs` — 1 test: PostAsync fails in closed fiscal year

**Key discovery:**
- All bugs being fixed by team were already landed — all 10 regression tests pass
- Closing entry filter already implemented via `excludeClosingEntries` parameter (default true for trial balance + income statement, false for balance sheet)
- PostAsync already checks closed FY
- Reversal date clamping already works (CreateReversalAsync uses FY EndDate as upper bound)
- P&L balance propagation already zeros out revenue/expense IB on copy

**Total test count: 149 (139 original + 10 new)**

### 2026-04-19: Full Test Coverage Assessment

**Test suite status:** 149 tests, all green. 20 test files (21 .cs files including TestFixture).

**Coverage gaps found by service:**

**JournalEntryService (most critical gaps):**
- `GetByFiscalYearAsync` — No tests (date range filter params untested)
- `GetByIdAsync` — No tests
- `DeleteDraftAsync` — No tests: success path, guard vs posted, guard vs closed FY, not-found all untested
- `GetDashboardStatsAsync` — No tests (counts drafts in total but only posted debits/credits)
- `UpdateAsync` — Partially tested: date out of FY range, account from wrong FY, closed FY guard all missing

**FiscalYearService (mostly untested CRUD layer):**
- `GetAllAsync` — No tests
- `GetByIdAsync` — No tests
- `GetActiveAsync` — No tests (important: "no active year" edge case)
- `CloseAsync` — No tests (direct close, distinct from YearEndClosingService.ExecuteClosingAsync)

**BasImportService — ZERO tests.** The entire service (ImportFromExcelAsync + TryParseAccountNumber) is untested. This includes: valid import, deduplication, sub-account parsing, invalid account numbers, broken Excel file, intra-import duplicate prevention, account class mapping.

**SieImportService (good coverage, some gaps):**
- Voucher with unknown account → warning path not tested
- Voucher with <2 lines → skip path not tested
- IB/UB for unknown account → warning path not tested
- Multi-year SIE with RAR=-1 (previous year balances) not explicitly tested

**SieExportService (good coverage):**
- FY not found → throws InvalidOperationException not tested
- CP437 Swedish chars in export not verified (only in import)

**YearEndClosingService (good coverage):**
- Net loss scenario in ExecuteClosingAsync not tested
- Preview with only expenses / only revenue not tested

**TestFixture design notes:**
- `TestFixture` does not expose `SieImportService` or `BasImportService` — test classes instantiate these manually, which is fine but inconsistent
- `TestFixture.GetTrialBalance` is called with default params everywhere; the `excludeClosingEntries=false` override tested only once
- `MakeEntry` creates entries with `IsPosted = false` by default but inserts directly (bypasses CreateAsync validation) — some test classes bypass this, which is acceptable but should be documented

**Priority summary:**
- 🔴 `DeleteDraftAsync` — zero tests, 3 guards plus happy path
- 🔴 `BasImportService.ImportFromExcelAsync` — entirely untested
- ⚠️ `UpdateAsync` — date/account/closed-year guards missing
- ⚠️ `FiscalYearService` CRUD — GetAllAsync, GetByIdAsync, GetActiveAsync, CloseAsync all untested
- 💡 `GetDashboardStatsAsync`, `GetByFiscalYearAsync`, SieImport warning paths

### 2026-04-19: Critical Coverage Gap Tests (DeleteDraftAsync + BasImportService)

**Files created:**
- `DeleteDraftAsyncTests.cs` — 6 tests covering all DeleteDraftAsync paths
- `BasImportServiceTests.cs` — 8 tests covering real BAS XLS import, deduplication, error handling

**DeleteDraftAsync tests (6):**
1. Entry not found → returns error
2. Posted entry → returns error (guard)
3. Closed fiscal year → returns error (guard)
4. Valid draft → returns null, entry removed
5. Valid draft → lines also removed (cascade)
6. Posted entry → entry NOT removed (safety check)

**BasImportService tests (8):**
1. Real BAS XLS file imports accounts successfully
2. Imported accounts exist in database with correct properties
3. Account class mapping correct (1xxx=Asset, 3xxx=Revenue, 5-6xxx=Expense)
4. All account numbers are 4-digit strings
5. Pre-existing account is skipped (deduplication)
6. Second import run skips all (idempotency)
7. Invalid stream returns error with message
8. Empty stream returns error

**Key findings:**
- BasImportService was moved to Infrastructure layer by Linus — test uses `KoalaBooks.Infrastructure.Services` namespace
- ExcelDataReader available transitively through Application project reference
- Real BAS XLS file at `resources/Kontoplan_K1_2018.xls` used for integration tests
- `TryParseAccountNumber` is private static — tested indirectly through ImportFromExcelAsync
- FindRepoRoot() helper walks up directory tree to locate resources/

**Total test count: 163 (149 existing + 14 new)**
