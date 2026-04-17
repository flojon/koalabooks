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
