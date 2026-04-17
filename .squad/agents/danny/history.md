# Danny — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Lead
- **Joined:** 2026-04-15T15:25:43.793Z

## Learnings

<!-- Append learnings below -->

### 2026-04-15 — Full Codebase Audit

**Architecture:**
- Clean Architecture: Domain → Application → Infrastructure → Web, orchestrated by .NET Aspire AppHost
- Domain is anemic (pure POCOs, no behavior/validation/events/interfaces)
- Application layer depends directly on `AppDbContext` (no repository/UoW abstractions) — violates dependency inversion
- All services are concrete classes; no interfaces for testability
- DTOs are inline at bottom of `JournalEntryService.cs`; the `DTOs/` folder is empty

**Key File Paths:**
- Domain entities: `src/KoalaBooks.Domain/Entities/` — Account, FiscalYear, JournalEntry, JournalEntryLine
- Domain enums: `src/KoalaBooks.Domain/Enums/AccountClass.cs`
- Application services: `src/KoalaBooks.Application/Services/` — AccountService, FiscalYearService, JournalEntryService
- Infrastructure: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`, `Services/CsvImportService.cs`, `Services/SieImportService.cs`, `Services/SieExportService.cs`
- Web pages: `src/KoalaBooks.Web/Components/Pages/` — Home, Accounts, Journal, FiscalYears, TrialBalance, GeneralLedger, IncomeStatement, BalanceSheet, SieImport, SieExport
- Layout: `src/KoalaBooks.Web/Components/Layout/MainLayout.razor`
- DI registration: `src/KoalaBooks.Web/Program.cs`
- Tests: `tests/KoalaBooks.Tests/` — 66 tests, 8 files

### 2026-04-17 — Year-End Closing (Bokslut) Design

**Key design decisions made:**
- Two-phase closing entries per BAS standard: P&L accounts → 8999, then 8999 → 2099
- New `YearEndClosingService` (separate from `FiscalYearService`) with Validate/Preview/Execute pattern
- Closing entries auto-posted (not drafts) because amounts are deterministic; preview step provides the safety net
- Auto-create accounts 8999/2099 if missing, to avoid blocking users
- Block closing if unposted drafts exist — user must consciously handle each one
- Data model: `FiscalYear.ClosedAt` (DateTime?) + `JournalEntry.IsClosingEntry` (bool) — minimal additions
- OutgoingBalance computed and persisted at closing time, fixing the current SIE-import-only gap
- 4-phase implementation plan: (1) data model migration, (2) closing service + tests, (3) UI, (4) integration polish

**Critical dependency:** P0 AccountClass fixes must land before implementation — closing logic depends on correct account classification.

**Trade-offs documented:**
- Two entries vs one: chose standard BAS pattern over simplicity for auditability and SIE compatibility
- Auto-post vs draft: chose auto-post with preview for UX cleanliness, mitigated by future reopen feature
- Bool vs enum for entry type: chose YAGNI bool, can migrate to enum when more types emerge
- No `PreviousFiscalYearId` FK: temporal ordering suffices, avoids maintenance burden

**ADR location:** `.squad/decisions/inbox/danny-yearend-closing-design.md`

**Bugs Found:**
- `SieExportService` is NOT registered in `Program.cs` but is `@inject`-ed in `SieExport.razor` → runtime DI exception
- `SieExport.razor` uses `JS.InvokeVoidAsync("eval", ...)` for file download — XSS risk
- `btn-warning` CSS class used in Journal.razor but never defined in `app.css`
- `CreateReversalAsync` doesn't check if fiscal year is closed
- Trial balance/reports aggregate ALL entries (draft + posted) — should only include posted

**Domain Model:**
- Accounts are scoped per fiscal year (duplicated rows with IB/UB per year)
- FiscalYear → Accounts (1:N, cascade delete), FiscalYear → JournalEntries (1:N, restrict delete)
- JournalEntry → JournalEntryLines (1:N, cascade), JournalEntryLine → Account (N:1, restrict)
- AccountClass enum: Asset=1, Liability=2, Revenue=3, Expense=4, Equity=8 (matches BAS first-digit convention)

**Test Coverage:**
- 66 tests, all passing. Good coverage of journal entry CRUD, reports, SIE import/export, CSV import
- Gaps: AccountService entirely untested, FiscalYearService.CloseAsync untested, no web layer tests
- Pattern: SQLite in-memory, xUnit, no mocks, significant constructor duplication across test classes

**Tech Stack:**
- .NET 10, Blazor Interactive Server, SQLite via EF Core 10, .NET Aspire 13.2.2
- CsvHelper 33.1.0, jsisie 2.7.0 (SIE format), xUnit 2.9.3
- OpenTelemetry, health checks, HTTP resilience via Aspire ServiceDefaults

### 2026-04-17 — Comprehensive Architecture Review

**Review output:** `.squad/decisions/inbox/danny-app-review.md`

**Current state:** Build clean (0 warnings), 139 tests passing, SQL Server migration complete.

**Key findings since last audit:**
- P0 bugs from April 15 audit are FIXED: AccountClass mapping, balance formulas, draft filtering, SIE DI registration, reversal closed-year guard, eval() XSS, date/account validation.
- Test count grew from 66 → 139 (110% increase). Year-end closing has 14 tests. SIE round-trip tested.
- `YearEndClosingService` is fully implemented and tested — validate/preview/execute with transaction safety.
- SQL Server migration completed (was SQLite). Tests remain on SQLite in-memory.

**Two critical items found:**
1. `FiscalYears.razor:134` calls `FiscalYearService.CloseAsync` — the old simple-close that skips closing entries, UB computation, and balance propagation. Must wire to `YearEndClosingService.ExecuteClosingAsync` or remove the button.
2. `Journal.razor:191` — `_activeFiscalYear!.Id` crashes with NullReferenceException when no active fiscal year exists. The null guard on line 11 only covers the render, not `OnInitializedAsync`.

**Architecture debt confirmed (known, accepted):**
- Application→Infrastructure dependency (all services use `AppDbContext` directly)
- No service interfaces
- JournalEntryService is a 553-line God class (CRUD + 5 report methods)
- DTOs inline in service files
- No ErrorBoundary components
- No pagination on any list
- Massive test constructor duplication (16 test classes)

**Roadmap gaps for next milestone:**
- Year-end closing UI page (service exists, UI doesn't)
- Verification number (BFL compliance)
- PostAsync fiscal-year-open guard
- AccountService validation
- Reopen fiscal year feature

### 2026-04-17 — Comprehensive Code Review (Session Batch)

**Scope:** 6 commits — P0 bug fixes, test refactor, UX overhaul, BAS import, PostgreSQL migration, Docker deployment.

**Build:** Clean (0 warnings). **Tests:** 149 passing (up from 139).

**Critical findings:**
1. `FiscalYearService.CloseAsync` (line 49) still exists as dead code — bypasses year-end closing logic. Should be removed or made `private` to prevent accidental use.
2. `NotificationService` is registered but only used nowhere — pages inject `ISnackbar` directly. Either adopt it everywhere or remove it.
3. Docker compose exposes port 8080 directly alongside Caddy on 80/443 — production should not expose 8080 externally.
4. No `.env` file ships by default — first deploy will fail unless user creates one. `.env.example` exists but documentation gap.
5. `DesignTimeDbContextFactory` contains hardcoded localhost PostgreSQL credentials — acceptable for dev tooling but noted.
6. No `UseHttpsRedirection` in `Program.cs` — Caddy handles TLS, so this is correct for the reverse-proxy pattern.
7. `GeneralLedger` report does NOT filter closing entries — may confuse users who see closing entries in the ledger.

**Architecture assessment:** Overall coherent. Clean Architecture layers respected. AppDbContext direct dependency (no repository interfaces) remains accepted tech debt. MudBlazor integration is clean with proper provider setup.

**Key validations confirmed working:**
- P&L IB=0 on year rollover (FiscalYearService line 88)
- Reversal date clamping to fiscal year bounds (JournalEntryService line 174)
- PostAsync fiscal year guard (line 129)
- SIE export filters to IsPosted only (line 59)
- FiscalYears.razor correctly wired to YearEndClosingService (line 209, 222)
- Journal.razor null crash fixed with early return (line 191-195)
