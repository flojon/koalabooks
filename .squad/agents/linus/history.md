# Linus — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Backend Dev
- **Joined:** 2026-04-15T15:25:43.798Z

## Learnings

<!-- Append learnings below -->

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
