# Reuben — History

> Session log of Reuben's work.

## Sessions

### 2025-07-15 — Roadmap Accounting Domain Review

Reviewed Danny's feature priority roadmap against the actual codebase. Confirmed all 5 of his bugs and found 5 additional domain-critical issues.

## Learnings

### Architecture / Key Decisions
- The codebase uses a single balance formula (IB + Debit - Credit) for ALL account classes. This is wrong for credit-normal accounts (liabilities, equity, revenue). Must fix before reports are trustworthy.
- AccountClass enum maps class 8 → Equity, but BAS class 8 is financial items (mix of revenue/expense). Real equity is in class 2 (20xx accounts). This fundamental mapping error affects every report.
- Account.OutgoingBalance is only populated by SIE import — no code computes UB from transactions. Year-end close (bokslut) must compute UB before propagating balances.
- All report queries (trial balance, balance sheet, income statement, general ledger, dashboard) lack `WHERE IsPosted = true` filtering. Draft entries pollute official reports.

### Key File Paths
- **Domain entities:** `src/KoalaBooks.Domain/Entities/` — Account, FiscalYear, JournalEntry, JournalEntryLine
- **AccountClass enum:** `src/KoalaBooks.Domain/Enums/AccountClass.cs` — needs expansion (only 5 values, missing FinancialIncome/Expense)
- **AccountClassMapper:** `src/KoalaBooks.Infrastructure/Services/AccountClassMapper.cs` — the broken class-8→Equity and class-2→Liability mapping
- **Report logic:** `src/KoalaBooks.Application/Services/JournalEntryService.cs` — all 5 reports live here
- **Year-end close:** `src/KoalaBooks.Application/Services/FiscalYearService.cs` — CloseAsync() is just a boolean flip
- **SIE export:** `src/KoalaBooks.Infrastructure/Services/SieExportService.cs` — not registered in DI, exports drafts
- **SIE import:** `src/KoalaBooks.Infrastructure/Services/SieImportService.cs` — robust, handles multi-year, balances
- **DI registration:** `src/KoalaBooks.Web/Program.cs` — missing SieExportService
- **SIE download:** `src/KoalaBooks.Web/Components/Pages/SieExport.razor` line 83 — eval() XSS risk
- **Sample kontoplan:** `sample-bas-kontoplan.csv` — 100 accounts covering classes 1-8

### User Preferences
- Jonas is building a Swedish bookkeeping application targeting BAS-kontoplan compliance
- SIE-4 import/export is a core feature (import works well, export has issues)
- Blazor Server with SQLite backend, .NET Aspire orchestration

### Patterns Noticed
- Tests are thorough for the happy path but miss sign-convention edge cases (credit-normal accounts)
- The BalanceSheetTests.BalanceSheet_AssetsEqualLiabilitiesPlusEquity_WhenBalanced test passes only because test data has no credit transactions on liability accounts — it only checks IB values
- SIE import correctly handles CP437→Latin-1 transcoding for Swedish characters_

### 2026-04-17 — Comprehensive Accounting Correctness Review
- Performed full review of all accounting logic. Findings delivered to `.squad/decisions/inbox/reuben-app-review.md`.
- **P0 fixes from prior session confirmed correct:** AccountClass mapping (2xxx split, 8xxx P&L), credit-normal balance formulas, draft filtering in reports all verified working.
- **New 🔴 bugs found:**
  1. Income statement zeroed after year-end closing — closing entries (IsClosingEntry=true, IsPosted=true) are included in P&L queries, netting all accounts to zero. Must filter out IsClosingEntry from report queries.
  2. SIE export includes draft entries — no IsPosted filter in SieExportService.ExportAsync. Violates BFL.
  3. Reversal date can land outside fiscal year — CreateReversalAsync uses DateTime.Today, bypasses date validation.
  4. P&L accounts propagated as IB when creating new FY before closing previous year — CopyAccountsFromPreviousYearAsync doesn't distinguish balance sheet from P&L.
  5. SIE export missing #ORGNR, #KSUMMA, empty verification series.
- **Year-end closing logic confirmed correct:** Two-phase P&L→8999→2099 is textbook Swedish. Auto-creation of result accounts, UB computation, and balance propagation all mathematically sound.
- **Missing BFL requirements:** Verification numbers (BFL 5 kap. 6§), verification series, mandatory entry text (BFL 5 kap. 7§), VAT/moms, SRU codes, accounting periods.
- **Test gaps:** No loss scenario in closing, no post-closing income statement test, no compound entries (3+ lines), no brutet räkenskapsår, no accounting equation verification after closing.
- **Key file paths reviewed:** YearEndClosingService.cs (new, correct), AccountClassMapper.cs (fixed, correct), SieExportService.cs (needs draft filter + ORGNR), all entity files, all 15 test files.
