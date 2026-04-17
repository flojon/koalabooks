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
