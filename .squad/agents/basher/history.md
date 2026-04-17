# Basher — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Infra Dev
- **Joined:** 2026-04-15T15:25:43.802Z

## Learnings

<!-- Append learnings below -->

- **DI registration lives in** `src/KoalaBooks.Web/Program.cs` — all services registered as `AddScoped<T>()`.
- **JournalEntryService** (`src/KoalaBooks.Application/Services/JournalEntryService.cs`) owns create, update, post, and reversal logic plus report queries (trial balance, general ledger, balance sheet, income statement).
- **Validation pattern:** `ValidateEntry()` handles structural checks (line count, debit=credit, no negatives). Fiscal-year-scoped checks (date range, account existence, closed year) happen in `CreateAsync`/`UpdateAsync` after loading the fiscal year.
- **Reversal pattern:** `CreateReversalAsync` creates a new posted entry with flipped debit/credit. Now includes `FiscalYear` in the query and rejects if `IsClosed`.
- **FiscalYear entity** has `StartDate`, `EndDate` (DateOnly), and `IsClosed` flag.
- **Accounts are scoped per fiscal year** — each account has a `FiscalYearId`. Validation must check accounts belong to the entry's fiscal year, not just that they exist globally.
- **AppHost project** has a pre-existing build issue (missing apphost binary) — unrelated to application code. Tests run via `dotnet test tests/KoalaBooks.Tests/`.
- **Database provider is SQL Server** (migrated from SQLite). AppHost orchestrates via `Aspire.Hosting.SqlServer`, Web uses `Aspire.Microsoft.EntityFrameworkCore.SqlServer`, Infrastructure uses `Microsoft.EntityFrameworkCore.SqlServer`.
- **Aspire wiring:** AppHost creates `AddSqlServer("sql").AddDatabase("koalabooks")`, Web project references it via `builder.AddSqlServerDbContext<AppDbContext>("koalabooks")`.
- **DesignTimeDbContextFactory** (`src/KoalaBooks.Infrastructure/Data/DesignTimeDbContextFactory.cs`) uses `UseSqlServer` with localdb connection for EF tooling outside Aspire.
- **Migrations live in** `src/KoalaBooks.Infrastructure/Data/Migrations/` with namespace `KoalaBooks.Infrastructure.Data.Migrations`.
- **Tests stay on SQLite** — `tests/KoalaBooks.Tests/` has its own `Microsoft.EntityFrameworkCore.Sqlite` reference, completely independent of the SQL Server provider.
