# Linus — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Backend Dev
- **Joined:** 2026-04-15T15:25:43.798Z

## Learnings

### 2026-04-18: BAS Chart of Accounts Import

- **`BasImportService`** in `src/KoalaBooks.Application/Services/BasImportService.cs` — method `ImportFromExcelAsync(Stream, int fiscalYearId)` returns `BasImportResult(ImportedCount, SkippedCount, Errors)`.
- **ExcelDataReader** (`ExcelDataReader` + `ExcelDataReader.DataSet`, v3.8.0) added to the Application project. Works for both .xls (BIFF) and .xlsx.
- **Key parsing detail**: ExcelDataReader returns numeric cells as `double` in .xls. Cast `(int)d` then `.ToString()` to get "1000", "1110", etc.
- **Two accounts per row**: BAS XLS has main account (cols B/C) and sub-account (cols E/F) in the same row. Both are processed in one pass.
- **Group header detection**: Non-numeric strings in col B (e.g. "10 Immateriella anläggningstillgångar") are silently skipped; only cells that parse as `double` in range 1000–9999 are accepted.
- **Duplicate guard**: Loads all existing `AccountNumber` values for the fiscal year into a `HashSet<string>` before iterating. Also adds to the set within the loop to catch in-file duplicates.
- **AccountClassMapper** (already in Infrastructure) reused for class assignment.
- **`Encoding.RegisterProvider`** is called both in `Program.cs` (for SIE) and defensively inside the service (idempotent).
- **UI**: `BasImport.razor` at `/import/bas`, MudBlazor components, fiscal year selector pre-selects active year, import triggers on file selection. Nav link added to Data section of `MainLayout.razor`.
- **Build**: 0 warnings, 0 errors.

<!-- Append learnings below -->

### 2026-04-18: CSV Import Removal — Replaced by BAS XLS Import
- Deleted `CsvImportService.cs` from Infrastructure/Services and removed CsvHelper NuGet package from Infrastructure.csproj.
- Removed CSV DI registration from Program.cs.
- Removed CSV upload button, `HandleFileUpload` method, and `CsvImportService` injection from `Accounts.razor`. Replaced with MudButton linking to `/import/bas`.
- Removed `CsvImportServiceTests` class (4 tests) from BookkeepingTests.cs. Test count went from 163 to 159.
- Empty-state alert in Accounts.razor now points users to BAS kontoplan import instead of CSV.
- **Build**: 0 errors, 0 warnings. **Tests**: 159 passed, 0 failed.

### 2026-04-15: P0 Accounting Bug Fixes
- **AccountClass enum** lives at `src/KoalaBooks.Domain/Enums/AccountClass.cs` — values: Asset=1, Liability=2, Revenue=3, Expense=4, Equity=8. Added `IsCreditNormal()` extension method.
- **AccountClassMapper** at `src/KoalaBooks.Infrastructure/Services/AccountClassMapper.cs` — maps BAS account numbers. Class 2 and 8 require sub-range inspection (second digit).
- **All report logic** lives in `JournalEntryService` (`src/KoalaBooks.Application/Services/JournalEntryService.cs`) — TrialBalance, GeneralLedger, BalanceSheet, IncomeStatement, DashboardStats.
- **Balance convention**: IB values are stored as positive magnitudes. Debit-normal accounts use `IB + Debit - Credit`, credit-normal use `IB + Credit - Debit`.
- **JournalEntry.IsPosted** (bool) controls whether entries appear in reports. No enum — just a flag. Default is false (draft).
- **Test pattern**: report tests must set `IsPosted = true` on entries, otherwise they won't appear in report queries.
- **BAS class 8**: Financial P&L items — 80xx-83xx are revenue (ränteintäkter), 84xx-89xx are expense (räntekostnader, skatt). They go on income statement, not balance sheet.

### 2026-04-17: P1 Delete Draft Journal Entry
- Added `DeleteDraftAsync(int entryId)` to `JournalEntryService` — returns `Task<string?>` (null = success, string = error).
- Follows same pattern as `PostAsync`: lookup entry, validate preconditions, mutate, save.
- Must `.Include(j => j.FiscalYear)` to check `IsClosed` — `FindAsync` alone won't load the nav property.
- EF cascade (`DeleteBehavior.Cascade`) handles `JournalEntryLine` removal automatically — no need to manually remove lines.
- Three guard checks: entry exists, not posted, fiscal year not closed.

### 2026-04-17: Year-End Closing (Bokslut) — Phase 1 & 2
- Added `ClosedAt` (DateTime?, nullable) to `FiscalYear` entity and `IsClosingEntry` (bool, default false) to `JournalEntry` entity.
- Created EF migration `AddYearEndClosingFields` — adds two columns, no breaking changes.
- `AppDbContext.OnModelCreating`: configured `IsClosingEntry` with `HasDefaultValue(false)` and `ClosedAt` as nullable DateTime.
- Created `YearEndClosingService` in `src/KoalaBooks.Application/Services/YearEndClosingService.cs` with three methods: `ValidateForClosingAsync`, `PreviewClosingAsync`, `ExecuteClosingAsync`.
- Result types defined as records in same file: `ClosingValidationResult`, `ClosingPreview`, `ClosingEntryPreview`, `ClosingLinePreview`, `ClosingResult`.
- **Balance computation**: Revenue (credit-normal) = IB + credits - debits; Expense (debit-normal) = IB + debits - credits. Reuses `IsCreditNormal()` extension. Includes IB (unlike income statement which is period-only).
- **P&L identification**: filter by `AccountClass ∈ {Revenue, Expense}`, exclude 8999 by account number (structural role per ADR).
- **Two closing entries**: Entry 1 "Resultatdisposition" closes P&L → 8999; Entry 2 "Årets resultat till eget kapital" transfers 8999 → 2099. Entry 2 skipped if netResult == 0.
- **Auto-create**: 8999 (Expense) and 2099 (Equity) created if missing. SaveChangesAsync needed after creation to get IDs for journal entry lines.
- **Outgoing balances**: B/S accounts get UB from IB + transactions; P&L accounts get UB = 0; 2099 adjusted by += netResult to include closing transfer.
- **Transaction safety**: `ExecuteClosingAsync` wraps all work in `BeginTransactionAsync`/`CommitAsync`. Two SaveChangesAsync calls within transaction (one for auto-created accounts, one for entries + closing).
- **Propagation**: Calls `FiscalYearService.PropagateBalancesToNextYearAsync` (public) via DI injection. Its internal SaveChangesAsync participates in the outer transaction.
- Closing entries are `IsPosted = true`, `IsClosingEntry = true`, date = `FiscalYear.EndDate`, sequential entry numbers via `MaxAsync + 1` pattern.
- Fixed pre-existing test mismatches: constructor signature, property names (`Success` not `IsSuccess`, `AccountNumber` not `AccountId`, `Error` not `Errors`).

### 2026-04-17: P0 Batch 2 — Code Review Bug Fixes
- **Closing entry filtering**: Report methods (`GetTrialBalanceAsync`, `GetBalanceSheetAsync`, `GetIncomeStatementAsync`) now accept `bool excludeClosingEntries` parameter. Income statement defaults to `true` (exclude), balance sheet defaults to `false` (include). Trial balance defaults to `true`. This prevents year-end closing entries from zeroing out the income statement.
- **Reversal date clamping**: `CreateReversalAsync` now clamps the reversal date to the fiscal year's date range. If today is after the fiscal year end, uses `FiscalYear.EndDate` instead of `DateTime.Today`.
- **P&L IB zeroing**: `CopyAccountsFromPreviousYearAsync` and `PropagateBalancesToNextYearAsync` now set `IncomingBalance = 0` for Revenue/Expense accounts. Only balance sheet accounts (Asset, Liability, Equity) carry forward their outgoing balance as incoming balance.
- **PostAsync closed-year guard**: `PostAsync` now loads the FiscalYear (via `.Include`) and rejects posting if `IsClosed == true`. Follows same pattern as `DeleteDraftAsync`.
- **Pattern**: All optional parameters use defaults that match existing caller behavior — no breaking changes to call sites or tests.

### 2026-04-18: Review Fixes — Danny & Reuben Findings
- **Fix 1**: Deleted dead `FiscalYearService.CloseAsync()` — it bypassed year-end closing (no bokslut entries, no UB computation). No callers existed; `FiscalYears.razor` already uses `YearEndClosingService.ExecuteClosingAsync()`.
- **Fix 2**: Added `excludeClosingEntries` parameter (default `true`) to `GetGeneralLedgerAsync` in `JournalEntryService`. Filters `IsClosingEntry == true` lines. Matches pattern in TrialBalance, BalanceSheet, IncomeStatement.
- **Fix 3**: Moved `BasImportService.cs` from Application layer to Infrastructure layer (`KoalaBooks.Infrastructure.Services` namespace). Moved ExcelDataReader NuGet packages from Application.csproj to Infrastructure.csproj. Updated DI registration and `BasImport.razor` using directives.
- **Fix 4**: Deleted unused `NotificationService` (ISnackbar wrapper). All pages inject `ISnackbar` directly. Removed DI registration, the class file, and the `KoalaBooks.Web.Services` using from Program.cs. Deleted empty Services directory.
- **Fix 5**: Added sign convention TODO comments to top of `SieImportService.cs` and `SieExportService.cs` documenting the conflict between SIE-4 negative IB for credit-normal accounts vs. positive-magnitude manual entry storage.
- **Build**: 0 errors, 0 warnings. **Tests**: 163 passed, 0 failed.
