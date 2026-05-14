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

### 2026-07-17 — Swedish Translation Review (Rusty's work)
- Reviewed all 11 Razor files for Swedish accounting terminology, natural phrasing, consistency, and BAS accuracy.
- **Overall quality: very good.** Rusty nailed the core accounting terms (verifikation, kontoplan, råbalans, resultaträkning, balansräkning, huvudbok, bokslut, räkenskapsår, debet/kredit, IB/UB).
- **REJECTED with 4 issues:**
  1. "BAS Kontoplan" → "BAS-kontoplan" (Accounts.razor line 33) — inconsistent with Home.razor
  2. "Stängd" → "Stängt" (FiscalYears.razor line 62) — wrong grammatical gender (räkenskapsår is neuter)
  3. "Öppen" → "Öppet" (FiscalYears.razor line 66) — wrong grammatical gender
  4. "Utkastverifikation raderad." → "Utkastet raderat." (Journal.razor line 293) — unnatural compound
- **Noted but not blocking:** Nav says "Journal" but page title says "Verifikationer" (minor mismatch, both valid). AccountClass enum values display in English (Asset, Liability, etc.) — code architecture issue, not translation scope.

### 2026-04-17 (Session 3) — Business Logic Accounting Correctness Review

Full review of all service files and entity models against Swedish accounting standards. Findings filed to `.squad/decisions/inbox/reuben-review-findings.md`.

**All P0 bugs from sessions 1 & 2 confirmed fixed.** No regressions.

**New findings this session:**

1. 🔴 **HIGH — Sign convention conflict (SIE import vs. manual entry):** SIE import stores IncomingBalance/OutgoingBalance as signed per SIE-4 (negative for credit-normal accounts). Manual entry stores them as positive (unsigned economic magnitude). This causes the balance sheet to show negative Skulder/Eget kapital totals for SIE-imported data, and the accounting equation test (A = L + E) fails for that data path. SIE export of manually created accounts also produces wrong-signed #IB/#UB. Fix: flip sign in SieImportService for credit-normal accounts; flip back in SieExportService.

2. 🟡 **MEDIUM — Income statement ignores IB on P&L accounts:** `GetIncomeStatementAsync` computes Revenue = Credit - Debit (transactions only), but closing service uses IB + Credit - Debit. If a P&L account has non-zero IB (possible after SIE import), the income statement understates revenue vs. what the closing entries actually close. Does not affect normally maintained books (IB=0 for P&L enforced on year rollover).

3. 🟡 **MEDIUM — FiscalYearService.CloseAsync() bypasses year-end closing:** `CloseAsync()` sets IsClosed=true with no closing entries or UB computation. If called directly instead of via YearEndClosingService, the year is sealed incorrectly. Should be marked obsolete or removed.

4. 🟢 **LOW — Missing test coverage:** No loss scenario test, no post-closing income statement = zero test, no post-closing balance sheet showing 2099, no test covering SIE-imported sign convention.

5. 🟢 **LOW — SIE export missing #ORGNR** (carry-over from session 2, still not fixed).

6. 🟢 **LOW — JournalEntryLine constraints only at service layer** — no DB constraints on debit/credit mutual exclusion.

**Items confirmed correct this session:**
- AccountClassMapper BAS mapping (1xxx, 20xx, 21xx-29xx, 3xxx, 4xxx-7xxx, 80xx-83xx, 84xx-89xx) — perfect.
- YearEndClosingService two-phase closing math — textbook Swedish bokslut.
- All five report methods use correct IsPosted + IsClosingEntry filtering with correct defaults.
- PropagateBalancesToNextYearAsync — P&L IB=0, balance sheet accounts carry UB forward.
- Reversal date clamping logic — correct.
- Entity model relationships — correct for double-entry.

**Overall rating: B+ (7.5/10).** Core accounting math is sound. Sign convention conflict is the last remaining architectural issue.

---

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
