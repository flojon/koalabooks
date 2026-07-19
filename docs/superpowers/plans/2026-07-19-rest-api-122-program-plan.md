# REST API Coverage (Issue #122) — Program Plan

> **For agentic workers:** This is a *program-level* plan, not a single bite-sized task plan. Each numbered stream below is a sub-project. Do not dispatch subagent-driven-development against this whole document at once — when a stream is greenlit, its owning agent must first do its own "inspect → files-to-change → dependencies → confirm approach" pass (per the task brief) and, for anything non-trivial, write its own bite-sized plan (superpowers:writing-plans) scoped to that stream alone before touching code.

**Goal:** Close out issue #122 — complete public REST API coverage for KoalaBooks' accounting resources — without redesigning any existing pattern.

**Architecture:** MVC API controllers in `KoalaBooks.Web/Controllers/Api/`, one per resource, calling existing Application-layer services. OpenIddict bearer-token auth, tenant scoping via `AppDbContext` global query filters keyed off `ICurrentUser.OrganisationId`. See section 1 for the full pattern.

**Spec:** GitHub issue #122 (body fetched 2026-07-19); original design spec `docs/superpowers/specs/2026-05-30-rest-api-design.md`; original v1 plan `docs/superpowers/plans/2026-05-31-rest-api-v1.md`.

## Global Constraints

- All routes under `/api/v1/` — no version bump.
- Controllers must not contain business logic — call an existing Application service; if the needed capability doesn't exist, **stop and flag it**, don't invent logic in the controller.
- Every controller: `[ApiController]`, `[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]`, `[Route("api/v1...")]`, constructor-DI of interfaces only.
- `[ProducesResponseType]` (or `[ProducesResponseType<T>]`) on every action, including 401/404/400 as applicable.
- Integration test via `WebApiFactory` + Testcontainers Postgres for every new endpoint — happy path, 401 without token, cross-tenant 404 where the resource is tenant-scoped.
- Tenant isolation: entities with a query filter (see section 1.4) are automatic; entities without one (e.g. `Account`) need an explicit transitive ownership check.
- No named authorization policies exist or should be introduced — plain authentication only.
- Avoid unrelated refactoring (no drive-by renames, no touching WASM/#275/#298 concerns from this issue).
- Work incrementally — one stream, one reviewable PR, at a time. Do not implement the whole issue in one pass.

---

## 1. Architecture Overview

### 1.1 Controllers
Location: `src/KoalaBooks.Web/Controllers/Api/`. Five exist today:

| Controller | Lines | Route root | Notes |
|---|---|---|---|
| `JournalEntriesController.cs` | 196 | `api/v1` (per-action nested paths) | CRUD + post/reverse; **preview-reversal NOT yet on `main`** — see 1.7 |
| `AccountsController.cs` | 57 | `api/v1` | GET-only today |
| `FiscalYearsController.cs` | 49 | `api/v1/fiscal-years` | GET + GET active only |
| `SupplierInvoicesController.cs` | 152 | `api/v1` | Full CRUD, missing from-entry/post/mark-paid/find-matching |
| `BankTransactionsController.cs` | 82 | `api/v1` | GET-only (list, unmatched-count, by-id) |

Uniform pattern (verified against all five files):
```csharp
[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]                                  // or a resource-rooted variant
public class XController : ControllerBase
{
    private readonly IXService _service;
    public XController(IXService service) => _service = service;
    // actions...
    private static XResponse MapX(X entity) => new(...);   // manual mapping, private static helper
}
```
- Nested-resource routes (`fiscal-years/{fiscalYearId:int}/accounts`) are spelled out per-action via `[HttpGet("...")]`, not via a nested `[Route]`.
- Error handling: services return `(T? Entity, string? Error)` tuples or a bare `string?`; controller maps a non-null error to `return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);`. `null` from a lookup → `return NotFound();`. No exceptions used for flow control.
- Pagination: manual in-memory `Skip/Take` wrapped in `PagedResult<T>` (`Models/Api/PagedResult.cs`) — issue #122 flags this as needing to become cursor-based eventually; **not in scope for any lettered stream below**, tracked separately.
- Some services signal not-found via a sentinel string (`SupplierInvoiceService.NotFoundMessage`) rather than a null tuple — controllers check `error == X.NotFoundMessage` before falling through to the generic 400. Follow this per-service, not a fixed rule.

### 1.2 DTOs
Location: `src/KoalaBooks.Web/Models/Api/` (namespace `KoalaBooks.Web.Models.Api`). **Not** `KoalaBooks.Application/DTOs/` (that folder is empty/unused).
- Responses: positional records, `{Noun}Response` (e.g. `AccountResponse`, `SupplierInvoiceResponse`).
- Requests: plain classes with `init` properties, `Create{Noun}Request` / `Update{Noun}Request` / `Reverse{Noun}Request`, validated with `System.ComponentModel.DataAnnotations` (`[Required]`, `[MinLength(1)]`) — `[ApiController]` auto-400s on failures, no FluentValidation, no manual `ModelState` handling.
- Enums serialize as strings via `[property: JsonConverter(typeof(JsonStringEnumConverter))]` on the response record property.
- `PagedResult<T>` — generic wrapper (`Items`, `Page`, `PageSize`, `TotalCount`). `CountResponse` — single-int wrapper used for badge-count endpoints.
- Mapping is 100% manual/inline. No AutoMapper.

### 1.3 Application services
Location: `src/KoalaBooks.Application/Services/` (one `I{Noun}Service.cs` + `{Noun}Service.cs` pair per interface), with a few lower-level ones in `src/KoalaBooks.Domain/Interfaces/` (`ICurrentUser`, `IBankImportService`, `IDocumentStorage`, `ISieExportService`) and one outlier in Infrastructure (`ISieImportService`).

**ISP pattern** (from the #224 interface-extraction project): `JournalEntryService` implements two segregated interfaces registered against the same instance:
```csharp
builder.Services.AddScoped<JournalEntryService>();
builder.Services.AddScoped<IJournalEntryService>(sp => sp.GetRequiredService<JournalEntryService>());
builder.Services.AddScoped<IJournalEntryReportingService>(sp => sp.GetRequiredService<JournalEntryService>());
```
Reuse this exact shape if a new service needs a CRUD/reporting split.

All services a new controller could need **already exist and are DI-registered** (`Program.cs:147-173`), confirmed by reading each interface directly:

| Interface | Location | Confirmed capability for #122 |
|---|---|---|
| `IAccountService` | Application | `CreateAsync`, `UpdateAsync`, `ToggleActiveAsync`, `GetMissingFromSourceAsync`, `CopyAccountsAsync` — all remaining Accounts verbs already implemented |
| `IFiscalYearService` | Application | `CreateAsync`, `GetAccountsAsync`, `PropagateBalancesToNextYearAsync` — all remaining FiscalYears verbs already implemented (no explicit "close year" method — see 5.B open question) |
| `ISupplierInvoiceService` | Application | `PostAsync`, `MarkAsPaidAsync`, `FindMatchingBankTransactionsAsync`, `CreateFromEntryAsync`, `DeleteAsync` — all remaining verbs implemented |
| `IBankImportService` | Domain.Interfaces | `ParseFile`, `BuildPreviewAsync`, `ImportAsync`, `GetUnmatchedAsync`, `SetStatusAsync`, `MatchToEntryAsync`, `SuggestContraAccountAsync` — all remaining verbs implemented |
| `ICustomerService` | Application | `GetAllAsync`, `CreateAsync`, `UpdateAsync`, `DeactivateAsync` — full Customers resource implemented |
| `ICustomerInvoiceService` | Application | `GetAllAsync(fiscalYearId)`, `GetByIdAsync`, `CreateAsync`, `PostAsync`, `FindMatchingBankTransactionsAsync`, `MarkAsPaidAsync`, `DeleteAsync` — list-by-fiscal-year and by-id are implemented; only `CreateFromEntryAsync` is a genuine gap (unlike `ISupplierInvoiceService`, which has one) |
| `IDocumentService` | Application | `UploadAsync`, `UpdateMetadataAsync`, `GetPendingAsync`/`GetPendingCountAsync`, `GetLinkedAsync`, `GetDownloadAsync`, `DeleteAsync`, `LinkAsync`, `UploadAndLinkAsync`, `UploadZipAsync` — full Documents resource implemented |
| `CustomerInvoicePdfGenerator` | `KoalaBooks.Web/Services/` (static, **not DI**) | `byte[] Generate(CustomerInvoice invoice)` — already used once from `Program.cs:251`; call directly, no registration needed |
| `IYearEndClosingService` | Application | `ValidateForClosingAsync`, `PreviewClosingAsync`, `ExecuteClosingAsync` — full Year-end closing resource implemented |
| `IVoucherGapService` | Application | `FindGapsAsync`, `GetUnexplainedGapsAsync`, `AddExplanationAsync`, `GetExplanationsAsync` — full Voucher gaps resource implemented |
| `IAccountMappingService` | Application | `BuildMappingAsync`, `ApplyMappingAsync` — full Account mapping resource implemented |
| `IOrganisationService` | Application | `GetCurrentAsync`, `UpdateAsync(name, orgNumber)` — full Organisation profile resource implemented |
| `ISieImportService` | **Infrastructure** (outlier namespace) | `Parse`, `GetPreviewAsync`, `ImportAllAsync`, `ImportFiscalYearAsync` — capability exists but see 5.H open question on sync-vs-async shape |
| `ISieExportService` | Domain.Interfaces | `ExportAsync(fiscalYearId, companyName?)` → `byte[]` — full SIE export implemented |
| `IJournalEntryReportingService` | Application | `GetTrialBalanceAsync`, `GetAccountLedgerAsync`, `GetGeneralLedgerAsync`, `GetComputedBalancesAsync`, `GetAccountIdsWithTransactionsAsync`, `GetBalanceSheetAsync`, `GetIncomeStatementAsync`, `GetVatReportAsync`, `GetDashboardStatsAsync` — **every report endpoint in the ticket has a backing method already** |
| `IVatReportCsvExporter` | Application (singleton) | backs the VAT report's CSV variant if needed |
| **`IBulkJournalImportService`** | **Does not exist** | Genuine gap — batch journal-entry import has no Application-layer service. Must be designed, not invented ad hoc in a controller. |

### 1.4 Tenant scoping
EF Core global query filters on `AppDbContext`, keyed off `ICurrentUser.OrganisationId` (sourced from the JWT `org_id` claim via `HttpContextCurrentUser`). Filtered entities: `FiscalYear`, `BankTransaction`, `JournalEntry`, `JournalEntryLine`, `SupplierInvoice`, `Customer`, `CustomerInvoice`, the voucher-gap entity, `Document`. **`Account` has no direct filter** — ownership must be verified transitively via its `FiscalYear` (see `AccountsController.cs:29-30,49-50` for the exact pattern to copy). Cross-tenant access should always resolve to 404, never 403 — this is deliberate and tested (`ApiTests.cs:190,264,423`).

### 1.5 Auth
OpenIddict OAuth2/JWT bearer (not cookies) for the API — `[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]` on every controller, no named policies. Password grant (`/connect/token`) is what integration tests use; the WASM cookie-bridge (#292/#294) mints tokens via a separate authorization_code flow but produces identically-shaped principals — irrelevant to how new controllers authorize, just reuse the same attribute verbatim.

### 1.6 Integration tests
`tests/KoalaBooks.Tests/Api/WebApiFactory.cs` (WebApplicationFactory<Program> + Testcontainers connection string) + `tests/KoalaBooks.Tests/PostgresContainerFixture.cs` (shared `postgres:17-alpine` container, unique DB per fixture). Representative file: `tests/KoalaBooks.Tests/Api/ApiTests.cs` (532 lines) — `IAsyncLifetime` seeding pattern, `GetBearerTokenAsync()`/`AuthenticatedClientAsync()` helpers, `SeedSecondTenantAsync()` for cross-tenant 404 tests. Known gotcha (documented at `ApiTests.cs:472-483`): service calls that depend on `ICurrentUser`/query filters can't be invoked directly from a manually-created `IServiceScope` in test setup (no ambient `HttpContext` → filters exclude everything) — use `db.Set<T>().IgnoreQueryFilters()` for out-of-band seeding instead.

### 1.7 OpenAPI
Built-in `Microsoft.AspNetCore.OpenApi` (`AddOpenApi()`/`MapOpenApi()` in `Program.cs`), Scalar UI in Development only. No XML doc comments, no Swashbuckle/NSwag, no checked-in spec file — metadata comes entirely from `[ProducesResponseType]` + DTO shape. Nothing to "keep in sync" beyond the attributes themselves.

### 1.8 Known in-flight work — do not duplicate
- **PR #299** (`worktree-issue-122-api-coverage` → `main`, currently **OPEN/draft, not merged**) adds `POST /api/v1/journal-entries/{id}/preview-reversal` via `IJournalEntryService.PreviewReversalAsync`. The issue checklist marks this `[x]` but it is **not on `main` yet** — confirmed by reading `JournalEntriesController.cs` on `main`, which has no such action. No stream below should touch `JournalEntriesController.cs`; if it needs to change for an unrelated reason, rebase on or coordinate with #299 first, don't silently reimplement the same route.

---

## 2. Endpoint Inventory (from issue #122, annotated)

Legend: ✅ merged on `main` · 🟡 open PR (not merged) · ⬜ not started · 🧩 needs a new/extended service first

| Resource | Endpoint | Status | Backing service method |
|---|---|---|---|
| **Accounts** | POST create | ⬜ | `IAccountService.CreateAsync` |
| | PUT update | ⬜ | `IAccountService.UpdateAsync` |
| | POST toggle-active | ⬜ | `IAccountService.ToggleActiveAsync` |
| | POST copy-accounts | ⬜ | `IAccountService.CopyAccountsAsync` |
| | GET missing-from-source | ⬜ | `IAccountService.GetMissingFromSourceAsync` |
| **Fiscal years** | GET active | ✅ (PR #273) | `IFiscalYearService.GetActiveAsync` |
| | POST create | ⬜ | `IFiscalYearService.CreateAsync` |
| | GET accounts-for-year | ⬜ | `IFiscalYearService.GetAccountsAsync` |
| | POST propagate-balances | ⬜ | `IFiscalYearService.PropagateBalancesToNextYearAsync` |
| | POST close year | ⬜ 🧩 | no dedicated method found — likely means Year-end closing's `ExecuteClosingAsync`; confirm with user (5.B) whether this is a distinct action or the same as Year-end closing execute |
| **Journal entries** | update, post | ✅ (PR #273) | `IJournalEntryService` |
| | preview-reversal | 🟡 PR #299 open | do not duplicate (1.8) |
| **Supplier invoices** | list/by-id/create/update/delete | ✅ (PR #272) | `ISupplierInvoiceService` |
| | POST from-entry | ⬜ | `CreateFromEntryAsync` |
| | POST post | ⬜ | `PostAsync` |
| | POST mark-paid | ⬜ | `MarkAsPaidAsync` |
| | GET find-matching-bank-tx | ⬜ | `FindMatchingBankTransactionsAsync` |
| **Bank transactions** | list-by-fiscal-year, by-id | ✅ (PR #272) | `IBankImportService` |
| | GET unmatched | ⬜ | `GetUnmatchedAsync` |
| | POST parse-preview | ⬜ | `ParseFile` + `BuildPreviewAsync` |
| | POST import | ⬜ | `ImportAsync` |
| | POST suggest-contra | ⬜ | `SuggestContraAccountAsync` |
| | POST set-status | ⬜ | `SetStatusAsync` |
| | POST match-to-entry | ⬜ | `MatchToEntryAsync` |
| **Customers** | list/by-id/create/update/deactivate | ⬜ (new controller) | `ICustomerService` (verify `GetByIdAsync` exists — not seen in interface dump, see 5.E) |
| **Customer invoices** | list/by-id/create/from-entry/post/mark-paid/find-matching/delete/pdf | ⬜ (new controller) | `ICustomerInvoiceService` + `CustomerInvoicePdfGenerator.Generate` (list/by-id confirmed implemented; from-entry is the only confirmed gap, see 5.E) |
| **Documents** | pending list+count/upload/upload-zip/linked/link/metadata/delete/download | ⬜ (new controller) | `IDocumentService` — every method already exists |
| **Organisation profile** | GET current / PUT update | ⬜ (new controller, smallest stream) | `IOrganisationService` |
| **SIE import/export** | parse+preview/import-all/export | ⬜ 🧩 | `ISieImportService`/`ISieExportService` exist, but see cross-ref with #279 (5.H) on sync vs async shape |
| **Account mapping** | build-mapping/apply-mapping | ⬜ | `IAccountMappingService` |
| **Year-end closing** | preview-closing/execute-closing | ⬜ | `IYearEndClosingService` |
| **Voucher gaps** | gaps/explanations/add-explanation | ⬜ | `IVoucherGapService` |
| **Reports** | dashboard/balance-sheet/income-statement/trial-balance/general-ledger (3 sub-endpoints)/vat-report | ⬜ (new controller) | `IJournalEntryReportingService` — every method already exists |
| **Bulk journal import** | POST batch | ⬜ 🧩 | `IBulkJournalImportService` does not exist — needs design |
| **Infra** | cursor-based pagination | ⬜ (not assigned — cross-cutting, out of scope for lettered streams) | — |

---

## 3. Dependency Map

```
Infra/Auth (done: OpenIddict API client #120, PR #272)
   │
   ├── Agent B: Accounts + FiscalYears completion  — no dependency on other streams
   │        (FiscalYears "close year" ambiguity may depend on Agent H's Year-end-closing scope)
   │
   ├── Agent C: Supplier invoices remaining verbs   — independent; touches only SupplierInvoicesController.cs
   │
   ├── Agent D: Bank transaction/import endpoints   — independent; touches only BankTransactionsController.cs
   │        (shares BankTransaction entity with Agent C's find-matching-bank-tx and Agent E's
   │         customer-invoice find-matching-bank-tx — same read-only method on 3 services, no write conflict)
   │
   ├── Agent E: Customers + Customer invoices        — new controllers; independent
   │        (must verify ICustomerInvoiceService/ICustomerService gaps before starting, see 5.E)
   │
   ├── Agent F: Documents                             — new controller; independent
   │        (upload-zip returns a background-job handle — same status-polling shape question as Agent H's SIE import)
   │
   ├── Agent G: Reports                               — new controller, read-only; fully independent, zero service gaps
   │
   ├── Agent H: SIE import/export, account mapping,   — mixed independence:
   │            year-end closing, voucher gaps,          - account mapping / year-end closing / voucher gaps: independent, services exist
   │            bulk journal import                      - SIE import: blocked on a design decision cross-referenced with #279 (Hangfire)
   │                                                      - bulk journal import: blocked on designing IBulkJournalImportService
   │
   └── Organisation profile (unassigned in the A-I split above — smallest stream,
            fold into Agent B or Agent E, whichever finishes first, or hand to Agent A)

Agent A: conventions/DTO consistency/OpenAPI review — runs continuously alongside B-H,
         not sequentially before them (there's no new convention being introduced, just
         auditing each stream's PR against section 1 as they land)

Agent I: final audit — strictly after all of B-H (and A's last pass) land
```

No stream in B-H has a hard build-order dependency on another **except**:
- FiscalYears "close year" (Agent B) vs Year-end closing execute (Agent H) — same underlying action, one plan, resolve before either starts (5.B).
- SIE import shape (Agent H) — resolve against #279 before starting that one sub-item; the rest of Agent H's scope is unblocked.
- Bulk journal import (Agent H) — needs a short service-design pass before any controller code.

---

## 4. Recommended Execution Order

1. **Agent G — Reports** first: zero service gaps, zero ambiguity, purely additive read-only controller. Best "prove the pattern still holds" stream and a fast, safe win.
2. **Agent B — Accounts + FiscalYears completion**: also zero gaps except the one "close year" naming question — ask user (5.B), then proceed same-day.
3. **Agent C — Supplier invoices remaining verbs** and **Agent D — Bank transaction/import endpoints** in parallel: both independent, both just wiring existing service methods into existing controllers, no new controllers.
4. **Agent E — Customers + Customer invoices** and **Agent F — Documents**: both new controllers, need their own service-gap verification pass first (5.E, 5.F) but no cross-stream blockers.
5. **Agent H — SIE import/export, account mapping, year-end closing, voucher gaps, bulk journal import**: split internally — ship account-mapping/year-end-closing/voucher-gaps first (no gaps), hold SIE-import and bulk-journal-import until their respective design questions are answered.
6. **Organisation profile**: smallest stream, slot in wherever capacity opens (suggest folding into Agent B's PR or giving it to Agent E once Customers lands).
7. **Agent A — conventions/DTO/OpenAPI review**: not a phase, an ongoing pass — review each PR from 1-6 as it's opened, flag drift from section 1's patterns.
8. **Agent I — final audit**: only after every stream above has merged.

Agent A and Agent I are advisory/review roles, not implementation streams — they don't produce their own PRs, they annotate others'.

---

## 5. Sub-agent Assignment Plan

Each entry: scope, exact files, services consumed (all confirmed to exist unless flagged), and open questions that must be resolved with the user before that stream starts coding.

### Agent A — API conventions, DTO consistency, OpenAPI review
**Role:** Review-only, continuous. No files of its own. Checklist per incoming PR: routes under `/api/v1/`, `[ProducesResponseType]` present including error codes, DTO naming (`{Noun}Response`/`Create{Noun}Request`) matches section 1.2, manual-mapping-only (no AutoMapper sneaking in), tenant-check present for any entity without a query filter, no named auth policies introduced.

### Agent B — AccountsController + FiscalYearsController completion
**Files:** modify `src/KoalaBooks.Web/Controllers/Api/AccountsController.cs`, `FiscalYearsController.cs`; new DTOs in `Models/Api/` (`CreateAccountRequest`, `UpdateAccountRequest`, `CopyAccountsRequest`, `CreateFiscalYearRequest`); tests in `tests/KoalaBooks.Tests/Api/ApiTests.cs` (or a split file if it's grown too large — check current line count first).
**Services:** `IAccountService` (all methods exist), `IFiscalYearService` (all methods exist).
**Open question (5.B):** issue says FiscalYears needs "close year" but `IFiscalYearService` has no closing method — only `IYearEndClosingService.ExecuteClosingAsync` does that. Confirm with user: is "close year" meant to just be a thin FiscalYears-rooted route that calls `IYearEndClosingService`, or is it the same action Agent H is already building under Year-end closing (in which case Agent B should skip it entirely)?

### Agent C — Supplier invoices remaining verbs
**Files:** modify `SupplierInvoicesController.cs`; new DTOs (`SupplierInvoiceFromEntryRequest`, `PostSupplierInvoiceRequest`, `MarkSupplierInvoicePaidRequest`); tests.
**Services:** `ISupplierInvoiceService.CreateFromEntryAsync/PostAsync/MarkAsPaidAsync/FindMatchingBankTransactionsAsync` — all confirmed to exist.
**No open questions.**

### Agent D — Bank transaction/import endpoints
**Files:** modify `BankTransactionsController.cs`; new DTOs (`ParsePreviewRequest`/`Response`, `ImportBankTransactionsRequest`, `SetBankTransactionStatusRequest`, `MatchToEntryRequest`); tests.
**Services:** `IBankImportService` — every remaining verb (`GetUnmatchedAsync`, `ParseFile`+`BuildPreviewAsync`, `ImportAsync`, `SuggestContraAccountAsync`, `SetStatusAsync`, `MatchToEntryAsync`) confirmed to exist.
**Note:** `ParseFile` takes a raw `Stream` — confirm the multipart/form-data upload convention Agent F establishes for Documents (or vice versa, whichever lands first) so file-upload endpoints look consistent across streams; flag to Agent A.

### Agent E — Customers + Customer invoices
**Files:** new `CustomersController.cs`, `CustomerInvoicesController.cs`; new DTOs (`CustomerResponse`, `CreateCustomerRequest`, `UpdateCustomerRequest`, `CustomerInvoiceResponse`, `CreateCustomerInvoiceRequest`, etc.); tests.
**Services:** `ICustomerService`, `ICustomerInvoiceService`, `CustomerInvoicePdfGenerator.Generate` (static, no DI).
**Open question (5.E):** `ICustomerService` has no `GetByIdAsync` — only `GetAllAsync(int organisationId)`. `ICustomerInvoiceService` does have a fiscal-year-scoped list method (`GetAllAsync(int fiscalYearId)`) and `GetByIdAsync`, but no `CreateFromEntryAsync` (unlike `ISupplierInvoiceService`, which has one). Before writing controllers, Agent E must confirm whether Customers needs a by-id lookup added to the service (flag back, don't add it in the controller) and whether customer-invoice from-entry is actually in scope for this pass — if so, design it the same way `SupplierInvoiceService.CreateFromEntryAsync` was designed, per the "no duplicate business logic in controllers" rule.

### Agent F — Documents/file handling endpoints
**Files:** new `DocumentsController.cs`; new DTOs (`DocumentResponse`, `LinkDocumentRequest`, `UpdateDocumentMetadataRequest`); tests (multipart upload test pattern will be new to this test suite — check no existing precedent in `ApiTests.cs` first).
**Services:** `IDocumentService` — every method (`UploadAsync`, `UpdateMetadataAsync`, `GetPendingAsync`/`GetPendingCountAsync`, `GetLinkedAsync`, `GetDownloadAsync`, `DeleteAsync`, `LinkAsync`, `UploadAndLinkAsync`, `UploadZipAsync`) confirmed to exist.
**Open question:** `UploadZipAsync` returns `(int? RunId, string? Error)` — a background-job handle (ties into the `BackgroundJobRun`/poller infra from PR #285/#296). The REST response shape for "upload accepted, here's a job id, poll elsewhere" needs to match whatever Agent H picks for SIE import (see 5.H) — coordinate rather than each inventing its own polling envelope.

### Agent G — Reports endpoints
**Files:** new `ReportsController.cs`; new DTOs for each report shape (check `IJournalEntryReportingService` return types before naming — several likely already have suitable shapes without needing new response records); tests.
**Services:** `IJournalEntryReportingService` — every method (`GetDashboardStatsAsync`, `GetBalanceSheetAsync`, `GetIncomeStatementAsync`, `GetTrialBalanceAsync`, `GetGeneralLedgerAsync`, `GetComputedBalancesAsync`, `GetAccountIdsWithTransactionsAsync`, `GetAccountLedgerAsync`, `GetVatReportAsync`) confirmed to exist. `IVatReportCsvExporter` if a CSV variant is wanted.
**No open questions — cleanest stream, recommended to go first (section 4).**

### Agent H — SIE import/export, account mapping, year-end closing, voucher gaps, bulk journal import
**Files:** new `SieController.cs` (or split import/export), `AccountMappingController.cs`, `YearEndClosingController.cs`, `VoucherGapsController.cs`; a new `IBulkJournalImportService`/`BulkJournalImportService` pair in `src/KoalaBooks.Application/Services/` plus a controller action for it; tests for each.
**Services:** `IAccountMappingService`, `IYearEndClosingService`, `IVoucherGapService` — all fully implemented, no gaps, safe to build immediately. `ISieImportService`/`ISieExportService` exist but see open question below. `IBulkJournalImportService` doesn't exist at all.
**Open question (5.H-1, SIE):** issue comment on #122 explicitly flags that #279 (Hangfire-based async SIE import) may land first or second, and whichever lands first should design a generic job-status surface (`batch/job id, done/progress/error`) the other reuses as its REST status endpoint. **Do not build a synchronous "upload, wait, get result" SIE import endpoint** — confirm with the user whether #279 has landed yet and reuse its status shape, or if #122 goes first, design the status envelope with #279's future needs in mind.
**Open question (5.H-2, bulk import):** `IBulkJournalImportService` must be designed before any controller code — this is exactly the "if a required service capability does not exist, identify it, don't bypass the architecture" case from the task brief. Recommend: stop and write a short interface-design note (method signature, batch validation semantics — all-or-nothing vs partial success, response shape) and confirm with the user before implementing, rather than guessing at semantics for a financial bulk-write operation.

### Agent I — Final audit
**Role:** after B-H (and Organisation profile, wherever it lands) are merged — compare final endpoint set against section 2's table, confirm every ⬜ became ✅, confirm every new controller has integration tests covering happy-path/401/404-cross-tenant, confirm every action has `[ProducesResponseType]`, confirm `/openapi/v1.json` reflects every new route (`curl` it and diff against the endpoint table). Produces a gap report, not code.

---

## Self-review notes
- Spec coverage: every checklist item in issue #122's body maps to a row in section 2 and an agent in section 5, except the standalone "cursor-based pagination" infra item, which is explicitly called out as unassigned/out-of-scope for this pass (matches "work incrementally" — it's a cross-cutting change to an already-shipped endpoint, not new coverage).
- Three genuine ambiguities were found by reading the code rather than assumed away: FiscalYears "close year" vs Year-end closing overlap (5.B), `ICustomerService`'s missing `GetByIdAsync` and `ICustomerInvoiceService`'s missing `CreateFromEntryAsync` (5.E), and the SIE-import/#279 async-shape cross-reference plus the missing `IBulkJournalImportService` (5.H). All four are flagged to the user rather than resolved by invention, per the task brief's explicit instruction.
- PR #299 status (open, not merged) was verified via `gh pr view`, not assumed from the issue checklist — the checklist alone would have led every stream to falsely believe `JournalEntriesController.cs` was fully done.
