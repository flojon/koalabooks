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
