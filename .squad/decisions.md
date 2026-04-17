# Squad Decisions

## Active Decisions

### 2026-04-15: Feature Priority Roadmap (Danny + Reuben)
**By:** Danny (Lead), reviewed by Reuben (Accountant)
**Status:** Proposed — awaiting user approval

**P0 — Fix accounting-breaking bugs first:**
1. AccountClass enum mapping wrong (class 2 → all Liability, class 8 → Equity; should split equity/liability and treat class 8 as P&L)
2. Balance formulas use asset-normal math for all accounts (credit-normal accounts show inverted balances)
3. Reports include draft entries (should only aggregate posted transactions)
4. SieExportService not registered in DI (export page crashes)
5. Reversals bypass closed-year check
6. eval() in SIE download (XSS risk)
7. No date/account validation on journal lines
8. Verification number (verifikationsnummer) not implemented (required by BFL 5 kap. 6§)

**P1 — Complete the bookkeeping lifecycle:**
1. Proper year-end closing (bokslut) with closing entries to equity
2. Opening balance automation
3. Delete draft journal entries
4. Reopen fiscal year
5. Input validation hardening
6. Accounting periods within fiscal year

**P2 — Daily bookkeeping usability:**
1. VAT/moms handling (25/12/6%)
2. Search & filtering
3. Journal entry templates
4. PDF report export
5. Per-account ledger view (kontoutdrag)
6. SIE-4 export compliance verification
7. Verification series (verifikationsserier)

**Backlog:** Auth, multi-company, audit trail, Excel export, backup/restore

**Decision:** Fix P0 accounting math before building any new features. Reuben's review elevated AccountClass mapping and credit-normal balance formulas as the two most critical issues.

See full details: `.squad/decisions/inbox/danny-feature-priority-roadmap.md` and `.squad/decisions/inbox/reuben-roadmap-accounting-review.md`

---

## Merged Decision Inbox Entries (as of 2026-04-17)

### 2025-07-24: Journal Entry Validation Pattern (Basher)
**Status:** Implemented
- Date validation: CreateAsync and UpdateAsync now reject entries where the date is outside FiscalYear.StartDate–EndDate.
- Account validation: Both methods load all account IDs for the fiscal year and reject lines referencing accounts not in that set.
- Reversal closed-year guard: CreateReversalAsync now loads FiscalYear and rejects if IsClosed.
- Validation layering: Structural checks (debit=credit, min 2 lines) stay in ValidateEntry(). Fiscal-year-scoped checks run after loading the fiscal year from DB in the async methods.
- All three validations return error strings via the existing (Entry?, Error?) tuple pattern—no exceptions, no breaking changes to callers.

### 2026-04-17: Migrate from SQLite to SQL Server with Aspire (Basher)
**Status:** Implemented
- AppHost: Added Aspire.Hosting.SqlServer package (13.2.2), creates SQL Server resource, web project gets connection via reference.
- Infrastructure: Swapped Microsoft.EntityFrameworkCore.Sqlite → SqlServer, DesignTimeDbContextFactory uses UseSqlServer, deleted old SQLite migrations, generated fresh InitialCreate for SQL Server.
- Web: Swapped to Aspire.Microsoft.EntityFrameworkCore.SqlServer, Program.cs uses AddSqlServerDbContext.
- Tests: Remain on SQLite in-memory, unaffected.
- Migration: dotnet ef migrations add InitialCreate succeeded using DesignTimeDbContextFactory.
- Verification: dotnet build (0 warnings/errors), dotnet test (all pass).

### 2026-04-17: User directive—Journal account dropdown search (Jonas Flodén)
- In journal entries, account dropdowns must support searching by both account number and account name/description. User request for team memory.

### 2026-04-17: Backlog item—Multi-company support (Jonas Flodén)
- Multi-company (multi-tenant) support added to backlog. Each company would have its own chart of accounts, fiscal years, journal entries, etc. Not prioritized yet—single company is fine for now.

### 2026-04-17: User directive—Multi-company UX (Jonas Flodén)
- When multi-company support is implemented, a user should be able to switch between the companies they belong to. Implies: user identity, user-company membership, and a company switcher in the UI.

### 2026-04-15: Feature Priority Roadmap (Danny)
**Status:** Proposed
- Priority 1: Critical Bugs & Correctness (fix immediately)
  - Register SieExportService in DI
  - Reports should only include posted entries
  - CreateReversalAsync must check FiscalYear.IsClosed
  - Replace eval() in SIE export download
  - Validate journal entry dates fall within fiscal year
  - Validate journal line account belongs to same fiscal year
- Priority 2: Essential Bookkeeping Features (next sprint)
  - Year-end closing entries (bokslut)
  - Opening balance automation
  - Delete draft journal entries
  - Reopen fiscal year
  - Input validation hardening
- Priority 3: High-Value User Features (next quarter)
  - VAT / Moms handling
  - Search & filtering
  - Journal entry templates / recurring entries
  - PDF report export
  - Account ledger per account (kontoutdrag)
- Priority 4: Architecture & Quality (ongoing)
  - Extract service interfaces
  - Move DTOs to proper files
  - Add ErrorBoundary components
  - Add loading states / StreamRendering
  - Shared test base class
  - Test AccountService and CloseAsync
  - Add pagination
- Priority 5: Future Enhancements (backlog)
  - User authentication
  - Multi-company / multi-tenant
  - Audit trail / history
  - Excel export
  - Data backup / restore
  - Concurrency handling
  - Dark mode
- Consequences: Priority 1 items are bugs that should block any feature work; Priority 2 items complete the core bookkeeping lifecycle; Priority 3 items make the app genuinely useful for daily Swedish bookkeeping; Priorities 4-5 are investments that pay off as the app grows.

### 2026-04-17: Year-End Closing (Bokslut) Feature Design (Danny)
**Status:** Proposed
- Two-phase closing entries: 1) Close P&L accounts to 8999, 2) Transfer result to 2099.
- Outgoing balance computation for all accounts before closing.
- Opening balance automation for next fiscal year if it exists.
- 3-step closing flow: validation, preview, confirmation & execution.
- Data model: FiscalYear gains ClosedAt, JournalEntry gains IsClosingEntry.
- Edge cases: unposted drafts block closing, missing 8999/2099 auto-created, zero-result skips entry 2, etc.
- New YearEndClosingService orchestrates the process.

### 2026-04-17: DeleteDraftAsync follows PostAsync pattern (Linus)
**Status:** Implemented
- DeleteDraftAsync(int entryId) returns Task<string?>—null on success, error message on failure. Matches PostAsync signature.
- Three guards: entry must exist, must not be posted, fiscal year must not be closed. Uses EF cascade delete for lines.

### 2026-04-15: P0 Accounting Bug Fixes—Implementation Decision (Linus)
**Status:** Implemented
- AccountClass mapping: BAS class 2 now splits into Equity (20xx) and Liability (21xx-29xx). Class 8 maps to Revenue (80xx-83xx) or Expense (84xx-89xx).
- Credit-Normal Balance Formulas: Liability, Equity, and Revenue accounts now compute balances as IB + Credit - Debit. Asset and Expense remain IB + Debit - Credit.
- Draft Filtering: All report queries now filter WHERE IsPosted == true. Draft entries are invisible to all reports.
- Impact: Existing data with 8xxx accounts classified as Equity is unaffected; new accounts are mapped correctly. Balance sheet totals for credit-normal accounts now show correct values. Reports no longer include unposted draft entries.

### 2025-07-15: Reuben's Accounting Domain Review of Danny's Feature Roadmap
**Status:** Recommendation
- Confirms Danny's bug findings and adds two critical bugs: AccountClass mapping and balance formula for liabilities/equity.
- Recommends fixing accounting math first, then filtering out drafts, then lifecycle features.
- Provides detailed roadmap and domain correctness concerns.

### 2026-04-15: Reusable AccountSearchDropdown Component (Rusty)
**Status:** Implemented
- Pure Blazor component for account selection, searchable by number or name, keyboard accessible, globally registered.
- Pattern can be adapted for other searchable dropdowns.


## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
