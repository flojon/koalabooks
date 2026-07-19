# WASM bundle Infrastructure decoupling

## Problem

Testing PR #291 (the `/review` WASM PoC) shows the browser downloading `UglyToad.PdfPig`,
`Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore*`,
`Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`,
`OpenIddict.EntityFrameworkCore*`, and `ExcelDataReader*` — roughly 25MB of server-only,
EF/Npgsql-coupled code that can never run in a browser sandbox and is never invoked by the
WASM app.

## Root cause

`KoalaBooks.Client` (the WASM head) references `KoalaBooks.Components` (the shared Razor
component library used by both the Server and WASM render trees). `Components` in turn has a
single `ProjectReference` to `KoalaBooks.Application`, because ~25 of its ~26 pages inject
service interfaces (`IFiscalYearService`, `IJournalEntryService`, `ICustomerInvoiceService`,
etc.) that today live in the `KoalaBooks.Application.Services` namespace, alongside their
EF-coupled concrete implementations and DTOs. `Application` itself references
`KoalaBooks.Infrastructure` (where `AppDbContext`, Npgsql, EF Core, OpenIddict, Identity, and
`UglyToad.PdfPig` live).

`ProjectReference` is all-or-nothing per project, and Blazor's `InteractiveAuto`/
`InteractiveWebAssembly` render mode requires the WASM runtime to load the actual compiled
component type — so `Client` must reference the whole `Components` assembly, which drags along
the whole `Application → Infrastructure` chain regardless of which specific interfaces the one
WASM-eligible page (`/review`) actually calls.

This was confirmed empirically: removing `Components`'s *direct* reference to `Infrastructure`
(cleaning up 4 dead `@using KoalaBooks.Infrastructure.Services` statements, already done) did
not shrink the bundle at all, in either a clean Debug build or a Release publish — because the
`Application` reference alone was sufficient to pull in every downstream package.

## Fix

Move every service interface (and any DTOs/helpers co-located with it) that `Components` pages
inject out of `KoalaBooks.Application.Services` and into `KoalaBooks.Domain.Interfaces`. This
extends a pattern the codebase already uses for three services (`ISieExportService`,
`IBankImportService`, and `ISieImportService`, the last moved earlier in this investigation).
Concrete EF-backed implementations stay exactly where they are (`KoalaBooks.Application.Services`
implementing the moved interfaces, using `KoalaBooks.Infrastructure.Data.AppDbContext`) —
only `KoalaBooks.Web` (the Server host) ever references them.

Once every interface `Components` needs lives in `Domain`, `Components.csproj` **swaps** its
`ProjectReference` from `Application` to `Domain` — not a bare drop. 64 files under `Components/`
reference `KoalaBooks.Domain.Entities`/`.Enums`/`.Interfaces` directly today (e.g. `Accounts.razor`,
`Review.razor`, `JournalReviewSection.razor`), but only get them transitively through
`Components → Application → Domain`. Dropping the `Application` reference without adding a direct
one to `Domain` would break compilation across those 64 files the moment the reference is removed.
With the swap made, the `Application → Infrastructure` chain (and everything under it: Npgsql, EF
Core, PdfPig, ExcelDataReader, OpenIddict, Identity, DataProtection) is no longer reachable from
`Client`'s build graph at all — not just "possibly trimmed," but structurally absent.

This is a mechanical, non-behavioral change: no routing, render-mode, or DI-registration logic
changes for the Server host. It's larger in file count than a narrower fix would be, but it
carries far less risk than the alternative (splitting `Components` into two Razor Class Library
projects with separate routing wired into each host), which this investigation considered and
rejected because of this codebase's history of subtle WASM routing/render-mode bugs (see
`2026-07-16-wasm-auth-bridge-design.md` and related issues #292 and the `InteractiveAuto`
render-race finding from testing #291).

## Scope: what moves

14 interface files move from `KoalaBooks.Application/Services/` to
`KoalaBooks.Domain/Interfaces/`, each carrying any DTOs/records currently defined alongside its
concrete implementation:

| Interface | Co-located DTOs to extract | Currently in |
|---|---|---|
| `IAccountService` | — | `AccountService.cs` |
| `IAccountMappingService` | `MappingRow`, `ApplyMappingResult` | `AccountMappingService.cs` |
| `ICustomerInvoiceService` | — | `CustomerInvoiceService.cs` |
| `ICustomerService` | — | `CustomerService.cs` |
| `IDocumentProvider` | — | (interface only) |
| `IDocumentService` | `DocumentMeta`, `ZipImportResult` | `DocumentService.cs` |
| `IFiscalYearService` | — | `FiscalYearService.cs` |
| `IJournalEntryReportingService` | `TrialBalanceRow`, `GeneralLedgerAccountSection`, `GeneralLedgerRow`, `DashboardStats`, `BalanceSheetSection`, `BalanceSheetRow`, `IncomeStatementSection`, `IncomeStatementRow`, `VatReportData`, `VatReportSection`, `VatReportRow` | `JournalEntryService.cs` |
| `IJournalEntryService` | — | `JournalEntryService.cs` |
| `IOrganisationService` | — | `OrganisationService.cs` |
| `ISupplierInvoiceService` | — | `SupplierInvoiceService.cs` |
| `IVatReportCsvExporter` | — | (interface only) |
| `IVoucherGapService` | — (`VoucherGapExplanation` is already a Domain entity) | `VoucherGapService.cs` |
| `IYearEndClosingService` | `ClosingValidationResult`, `ClosingPreview`, `ClosingEntryPreview`, `ClosingLinePreview`, `ClosingResult` | `YearEndClosingService.cs` |

Plus one non-interface static helper, `VatQuarterHelper` (pure date math over
`Domain.Entities.FiscalYear`, no Infrastructure dependency), used by `VatReport.razor` via
`@using static`. Moves to `KoalaBooks.Domain.Interfaces` alongside the others.

Every one of these interfaces was checked and only references `KoalaBooks.Domain.Entities` /
`KoalaBooks.Domain.Enums` types plus BCL types (`Stream`, `DateOnly`, etc.) — none require a new
package dependency in `Domain` (unlike `ISieImportService`, which needed `jsisie` for its
`SieDocument` parameter and already got that added).

`JournalEntryExtensions` and `DemoDataSeeder` (also in `Application.Services`, both directly
coupled to `AppDbContext`) are **not** referenced by any `Components` page and stay put
untouched.

## Scope: what else changes

- **`KoalaBooks.Components.csproj`**: swap `ProjectReference` from `KoalaBooks.Application` to
  `KoalaBooks.Domain` (see "Fix" above — this is not a bare drop).
- **~25 `.razor` files**: `@using KoalaBooks.Application.Services` → `@using KoalaBooks.Domain.Interfaces`
  (mechanical; verified during implementation that nothing else from that namespace is still needed).
- **`KoalaBooks.Application` concrete service files**: `using KoalaBooks.Domain.Interfaces;`
  added where needed; co-located DTO definitions removed (now living in `Domain`).
- **`KoalaBooks.Web/Program.cs` + API controllers**: `using` statements updated to the new
  namespace; DI registrations (`AddScoped<IFiscalYearService, FiscalYearService>()`) are
  unchanged in behavior.
- **`KoalaBooks.Client/Program.cs` and the existing `Client/Services/*ApiService.cs` files**
  (`AccountApiService`, `FiscalYearApiService`, `JournalEntryApiService`): `using
  KoalaBooks.Application.Services` → `using KoalaBooks.Domain.Interfaces`. These already compile
  today only because `Client → Components → Application` transitively exposed the namespace; once
  `Components` no longer references `Application`, that path is gone.
- **`KoalaBooks.Tests`**: `using` statements updated wherever a test references one of the moved
  interface types directly (concrete classes are untouched and stay discoverable in
  `Application.Services`).
- **`KoalaBooks.Client/Program.cs`**: two new DI registrations (see below), in addition to the
  namespace fix above.

## The MainLayout gap: two new WASM API-client services

`Routes`'s render mode is set once for the whole tree (`App.razor`'s
`<Routes @rendermode="RenderModeForPage" />`), so when `/review` renders via WASM, `MainLayout`
renders via WASM too. `MainLayout` resolves `IBankImportService` and `ISupplierInvoiceService`
via `ScopeFactory.CreateAsyncScope()` for its nav badge counts
(`CountUnmatchedAsync`/`CountUnpaidAsync`). Today, `Client/Program.cs` only registers WASM
implementations for `IFiscalYearService`, `IAccountService`, and `IJournalEntryService` — so if
WASM ever actually wins the `InteractiveAuto` render race for `/review` (it doesn't locally per
earlier testing, but could in a real deployment), `MainLayout` would throw an unhandled
`InvalidOperationException` from the missing DI registration.

Add two new classes to `KoalaBooks.Client/Services/`, following the exact pattern already
established by `FiscalYearApiService`/`AccountApiService`/`JournalEntryApiService`:

- **`BankImportApiService : IBankImportService`** — only `CountUnmatchedAsync` gets a real
  implementation, calling the existing `GET api/v1/fiscal-years/{id}/bank-transactions/unmatched-count`
  endpoint. Every other `Task`-returning member returns `Task.FromException` wrapping a
  `NotSupportedException` (so the failure surfaces on `await`, like a real async call, rather than
  throwing synchronously at the call site); the one non-`Task` member (`ParseFile`) throws directly
  since there's no async signature to preserve.
- **`SupplierInvoiceApiService : ISupplierInvoiceService`** — only `CountUnpaidAsync` gets a real
  implementation, calling the existing `GET api/v1/fiscal-years/{id}/supplier-invoices/unpaid-count`
  endpoint. Same `Task.FromException`/`NotSupportedException` pattern for the rest.

Both REST endpoints already exist (`BankTransactionsController`, `SupplierInvoicesController`) —
no server-side API changes needed. Register both in `Client/Program.cs` next to the existing
three registrations.

## Verification

1. Full solution build (`dotnet build`) — confirms nothing else broke across Web/Tests/Client.
2. `dotnet test` — confirms the namespace-only renames didn't change behavior.
3. Clean rebuild of `KoalaBooks.Client` (`rm -rf bin obj` across the touched projects, then
   `dotnet build`) and inspect `wwwroot/_framework/*.wasm` — `PdfPig`, `Npgsql`, `EntityFrameworkCore`,
   `OpenIddict`, `Identity.EntityFrameworkCore`, `DataProtection.EntityFrameworkCore`, and
   `ExcelDataReader` assemblies must be absent. (This is the same check already used earlier in
   this investigation and is what caught the first fix's incompleteness — it's a reliable signal.)
4. `dotnet publish -c Release` and repeat the same `_framework` check, since Debug and Release
   build the framework folder independently.
5. Manual smoke test: run the app, load `/review`, confirm the page still works (drafts list
   loads, badge counts render in the nav) — this exercises both the Server-rendered path (today's
   actual behavior, per the `InteractiveAuto`-never-wins-locally finding) and confirms nothing
   broke for the common case.

## Follow-up (out of scope): `Application.Abstractions` split

Strictly, the 14 interfaces this doc moves (`IJournalEntryService`, `ICustomerInvoiceService`,
etc.) are use-case/orchestration ports, not domain contracts — Clean Architecture would keep
`Domain` to Entities/Enums (and true persistence ports) only, with these interfaces living in a
separate `Application.Abstractions` project (interfaces + DTOs, zero package references) sitting
between `Domain` and `Application`. `Components`/`Client` would reference `Application.Abstractions`
instead of `Domain`, achieving the same WASM-bundle decoupling without folding use-case ports into
the entity layer.

Not doing this now: it's a pure re-layering of files already in flight, buys no additional
bundle-size reduction over putting them in `Domain`, and adds a fifth `ProjectReference` hop.
Worth revisiting as a standalone architectural cleanup if `Domain.Interfaces` keeps growing and
starts feeling structurally distinct from `Entities`/`Enums`.
