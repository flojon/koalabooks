# Basher — History

## Core Context

- **Project:** A .NET 10 Blazor bookkeeping app with interactive server-side rendering, clean architecture, and SQLite persistence
- **Role:** Infra Dev
- **Joined:** 2026-04-15T15:25:43.802Z

## Learnings

<!-- Append learnings below -->

- **2026-04-18:** Removed `ports: - "8080:8080"` from the `web` service in `docker-compose.yml` to close a critical security hole. The app is now only accessible via the Caddy reverse proxy, ensuring all external traffic is encrypted and routed through Caddy. Caddy's config (`reverse_proxy web:8080`) continues to work via Docker's internal network.

- **SIE export draft filtering:** `SieExportService.ExportAsync` was exporting all `JournalEntries` including drafts. Fixed by adding `.Where(e => e.IsPosted)` before the `#VER`/`#TRANS` loop. The `#KONTO`, `#IB`, and `#UB` records are account-level (not entry-level) so they were unaffected. Only the verification/transaction records needed filtering.

- **DI registration lives in** `src/KoalaBooks.Web/Program.cs` — all services registered as `AddScoped<T>()`.
- **JournalEntryService** (`src/KoalaBooks.Application/Services/JournalEntryService.cs`) owns create, update, post, and reversal logic plus report queries (trial balance, general ledger, balance sheet, income statement).
- **Validation pattern:** `ValidateEntry()` handles structural checks (line count, debit=credit, no negatives). Fiscal-year-scoped checks (date range, account existence, closed year) happen in `CreateAsync`/`UpdateAsync` after loading the fiscal year.
- **Reversal pattern:** `CreateReversalAsync` creates a new posted entry with flipped debit/credit. Now includes `FiscalYear` in the query and rejects if `IsClosed`.
- **FiscalYear entity** has `StartDate`, `EndDate` (DateOnly), and `IsClosed` flag.
- **Accounts are scoped per fiscal year** — each account has a `FiscalYearId`. Validation must check accounts belong to the entry's fiscal year, not just that they exist globally.
- **AppHost project** has a pre-existing build issue (missing apphost binary) — unrelated to application code. Tests run via `dotnet test tests/KoalaBooks.Tests/`.
- **Database provider is SQL Server** (migrated from SQLite — then migrated to PostgreSQL 2026-04-17, see learnings below). AppHost now uses `Aspire.Hosting.PostgreSQL`, Web uses `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`, Infrastructure uses `Npgsql.EntityFrameworkCore.PostgreSQL`.
- **Aspire wiring:** AppHost creates `AddSqlServer("sql").AddDatabase("koalabooks")`, Web project references it via `builder.AddSqlServerDbContext<AppDbContext>("koalabooks")`.
- **DesignTimeDbContextFactory** (`src/KoalaBooks.Infrastructure/Data/DesignTimeDbContextFactory.cs`) uses `UseSqlServer` with localdb connection for EF tooling outside Aspire.
- **Migrations live in** `src/KoalaBooks.Infrastructure/Data/Migrations/` with namespace `KoalaBooks.Infrastructure.Data.Migrations`.
- **Tests stay on SQLite** — `tests/KoalaBooks.Tests/` has its own `Microsoft.EntityFrameworkCore.Sqlite` reference, completely independent of the SQL Server provider.

- **PostgreSQL migration (2026-04-17):** Switched from SQL Server to PostgreSQL across AppHost, Infrastructure, and Web. Key packages: `Aspire.Hosting.PostgreSQL`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`. `DesignTimeDbContextFactory` updated to `UseNpgsql`. Migrations regenerated.
- **EF Core version alignment gotcha:** When switching to `Npgsql.EFCore.PostgreSQL 10.0.1`, must also downgrade `Microsoft.EntityFrameworkCore.Design` from 10.0.5 to 10.0.4. Mismatch causes CS1705 (assembly version conflict) at compile time because Infrastructure's DLL gets compiled against 10.0.5 but transitive deps only resolve 10.0.4.
- **Docker deployment stack (2026-04-17):** Added `docker-compose.yml` (web + postgres + caddy), `Caddyfile`, `.env.example`, `src/KoalaBooks.Web/Dockerfile` (multi-stage SDK build), `.dockerignore`, `.github/workflows/deploy.yml` (GHCR publish + SSH deploy via appleboy/ssh-action), `backup.sh` (pg_dump with 30-day retention). `.env` added to `.gitignore`.
- **Database provider is now PostgreSQL** — AppHost uses `AddPostgres("postgres").WithDataVolume("koalabooks-postgres-data").WithLifetime(ContainerLifetime.Persistent).AddDatabase("koalabooks")`. Web uses `AddNpgsqlDbContext<AppDbContext>("koalabooks")`.
- **Container publish:** Web.csproj has `ContainerRepository=koalabooks-web` and `ContainerImageTag=latest` for SDK-native `dotnet publish /t:PublishContainer`.
