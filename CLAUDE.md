# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Run the app (Aspire orchestrates Postgres + the Web project)
aspire start                       # see .claude/skills/aspire/SKILL.md for the full CLI
aspire start --isolated            # in a worktree, to avoid port/volume conflicts

# Run all tests (integration tests spin up real Postgres via Testcontainers - Docker must be running)
dotnet test

# Run a single test / fixture
dotnet test --filter "FullyQualifiedName~BookkeepingTests"
dotnet test --filter "DisplayName~SomeSpecificTest"

# EF Core migrations (Infrastructure holds migrations, Web is the startup project)
dotnet ef migrations add <Name> --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web
dotnet ef database update --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web
```

There is no lint/format command configured yet.

## Architecture

Strict layering, each layer only depends on the ones to its left:

`Domain <- Infrastructure <- Application <- Components/Web`

- **Domain** — entities, enums, and interfaces (`ICurrentUser`, `IDocumentStorage`, `IDocumentExtractor`, `IDocumentExtractionQueue`). No dependencies on anything else in the solution.
- **Infrastructure** — `AppDbContext` (EF Core/Npgsql), migrations, and concrete services (SIE import/export, bank/BAS import, document storage/extraction, PDF text extraction).
- **Application** — business-logic services (`AccountService`, `JournalEntryService`, `FiscalYearService`, `YearEndClosingService`, `CustomerInvoiceService`, ...) and Hangfire job definitions. Depends on Domain + Infrastructure.
- **Components** — the Blazor Razor Component library (pages/layout/shared UI, MudBlazor). Depends on Application + Infrastructure.
- **Web** — the ASP.NET Core host: `Program.cs` wires up DI, auth, Hangfire, Aspire, and hosts both the Blazor components and a small set of Minimal API/MVC controllers (`Controllers/Api/*`) for programmatic access.
- **AppHost** — .NET Aspire orchestrator; the only place that knows about the Postgres container (`AppHost.cs`). `AppHostSupport` holds shared logic (e.g. Postgres volume naming) referenced by both AppHost and the test project.

### Domain model

Double-entry bookkeeping over the Swedish BAS chart of accounts: `Organisation` -> `FiscalYear` -> `JournalEntry` -> `JournalEntryLine` (debit=credit enforced), `Account` classified via `AccountClass` (derived from the leading digit per BAS). `LegalForm` is set once at organisation registration and drives which contra accounts (e.g. 2013/2018 for enskild firma, 2893 for aktiebolag) default in the UI — it's immutable after registration. Supplier/customer invoices and bank transactions (`BankImportService`) ultimately resolve to journal entries. Documents (receipts/invoices) go through an extraction pipeline (`IDocumentExtractor` -> `CompositeExtractor` combining filename + PDF text extraction) queued via Hangfire (`IDocumentExtractionQueue`).

### Auth

ASP.NET Core Identity (cookie auth for the Blazor UI) + OpenIddict (password/refresh/authorization-code flows for the API, `/connect/token`). `ICurrentUser` is the seam between Domain and the concrete `HttpContextCurrentUser` (Web) / `LocalCurrentUser` (design-time tooling).

### Gotchas worth knowing before touching data access

- `AppDbContext` is registered **unpooled** — its `ICurrentUser` scoped dependency can't be resolved by a pooled context's activator (see comment in `Program.cs`).
- The Npgsql resilient execution strategy (`EnrichNpgsqlDbContext`) forbids manually opened transactions (`BeginTransactionAsync`) outside `CreateExecutionStrategy` — it'll throw. Retry-sensitive services have `*RetryStrategyTests` covering this.
- Blazor Server keeps a long-lived scoped `DbContext` per circuit; a stale identity-mapped entity (wrong `xmin`) surfaces as an unhandled `DbUpdateConcurrencyException` that kills the circuit rather than a friendly error.
- Integration tests (`tests/KoalaBooks.Tests`) use `Testcontainers.PostgreSql` against the real EF pipeline; component tests (`tests/KoalaBooks.ComponentTests`) use bUnit + NSubstitute against Razor components directly.

### Deployment

PR preview environments run via `docker-compose.pr-preview.yml` + Caddy on a dedicated VM; see `.github/workflows/pr-preview.yml` / `pr-preview-cleanup.yml`.
