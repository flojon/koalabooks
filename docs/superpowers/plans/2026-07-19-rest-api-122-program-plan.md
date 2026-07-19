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
| `IFiscalYearService` | Application | `CreateAsync`, `GetAccountsAsync`, `PropagateBalancesToNextYearAsync` — all remaining FiscalYears verbs already implemented. **Resolved (5.B):** "close year" is not a FiscalYears method — it's `IYearEndClosingService`'s validate/preview/execute triad, nested under the fiscal-years route but owned entirely by Agent H |
| `ISupplierInvoiceService` | Application | `PostAsync`, `MarkAsPaidAsync`, `FindMatchingBankTransactionsAsync`, `CreateFromEntryAsync`, `DeleteAsync` — all remaining verbs implemented |
| `IBankImportService` | Domain.Interfaces | `ParseFile`, `BuildPreviewAsync`, `ImportAsync`, `GetUnmatchedAsync`, `SetStatusAsync`, `MatchToEntryAsync`, `SuggestContraAccountAsync` — all remaining verbs implemented |
| `ICustomerService` | Application | `GetAllAsync`, `CreateAsync`, `UpdateAsync`, `DeactivateAsync` — full Customers resource implemented |
| `ICustomerInvoiceService` | Application | `GetAllAsync(fiscalYearId)`, `GetByIdAsync`, `CreateAsync`, `PostAsync`, `FindMatchingBankTransactionsAsync`, `MarkAsPaidAsync`, `DeleteAsync` — list-by-fiscal-year and by-id are implemented. **Resolved (5.E):** `CreateFromEntryAsync` is a genuine gap (unlike `ISupplierInvoiceService`, which has one) but is **deferred to a follow-up issue**, out of scope for this pass |
| `IDocumentService` | Application | `UploadAsync`, `UpdateMetadataAsync`, `GetPendingAsync`/`GetPendingCountAsync`, `GetLinkedAsync`, `GetDownloadAsync`, `DeleteAsync`, `LinkAsync`, `UploadAndLinkAsync`, `UploadZipAsync` — full Documents resource implemented |
| `CustomerInvoicePdfGenerator` | `KoalaBooks.Web/Services/` (static, **not DI**) | `byte[] Generate(CustomerInvoice invoice)` — already used once from `Program.cs:251`; call directly, no registration needed |
| `IYearEndClosingService` | Application | `ValidateForClosingAsync`, `PreviewClosingAsync`, `ExecuteClosingAsync` — full Year-end closing resource implemented |
| `IVoucherGapService` | Application | `FindGapsAsync`, `GetUnexplainedGapsAsync`, `AddExplanationAsync`, `GetExplanationsAsync` — full Voucher gaps resource implemented |
| `IAccountMappingService` | Application | `BuildMappingAsync`, `ApplyMappingAsync` — full Account mapping resource implemented |
| `IOrganisationService` | Application | `GetCurrentAsync`, `UpdateAsync(name, orgNumber)` — full Organisation profile resource implemented |
| `ISieImportService` | **Infrastructure** (outlier namespace) | `Parse`, `GetPreviewAsync`, `ImportAllAsync`, `ImportFiscalYearAsync` — capability exists. **Resolved (5.H-1):** Agent H wraps it in a new `SieImportJob` (Hangfire), reusing the `BackgroundJobRun` envelope from PR #285 (see 1.9). Subsumes #279 — close it as superseded once merged |
| `ISieExportService` | Domain.Interfaces | `ExportAsync(fiscalYearId, companyName?)` → `byte[]` — full SIE export implemented |
| `IJournalEntryReportingService` | Application | `GetTrialBalanceAsync`, `GetAccountLedgerAsync`, `GetGeneralLedgerAsync`, `GetComputedBalancesAsync`, `GetAccountIdsWithTransactionsAsync`, `GetBalanceSheetAsync`, `GetIncomeStatementAsync`, `GetVatReportAsync`, `GetDashboardStatsAsync` — **every report endpoint in the ticket has a backing method already** |
| `IVatReportCsvExporter` | Application (singleton) | backs the VAT report's CSV variant if needed |
| **`IBulkJournalImportService`** | **Does not exist** | Genuine gap — batch journal-entry import has no Application-layer service. **Resolved (5.H-2):** all-or-nothing transactional semantics (deliberately different from the partial-success convention below); exact method signature/DTO shape still needs a short design pass before controller code. |

### 1.4 Tenant scoping
EF Core global query filters on `AppDbContext`, keyed off `ICurrentUser.OrganisationId` (sourced from the JWT `org_id` claim via `HttpContextCurrentUser`). Filtered entities: `FiscalYear`, `BankTransaction`, `JournalEntry`, `JournalEntryLine`, `SupplierInvoice`, `Customer`, `CustomerInvoice`, the voucher-gap entity, `Document`. **`Account` has no direct filter** — ownership must be verified transitively via its `FiscalYear` (see `AccountsController.cs:29-30,49-50` for the exact pattern to copy). Cross-tenant access should always resolve to 404, never 403 — this is deliberate and tested (`ApiTests.cs:190,264,423`).

### 1.5 Auth
OpenIddict OAuth2/JWT bearer (not cookies) for the API — `[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]` on every controller, no named policies. Password grant (`/connect/token`) is what integration tests use; the WASM cookie-bridge (#292/#294) mints tokens via a separate authorization_code flow but produces identically-shaped principals — irrelevant to how new controllers authorize, just reuse the same attribute verbatim.

### 1.6 Integration tests
`tests/KoalaBooks.Tests/Api/WebApiFactory.cs` (WebApplicationFactory<Program> + Testcontainers connection string) + `tests/KoalaBooks.Tests/PostgresContainerFixture.cs` (shared `postgres:17-alpine` container, unique DB per fixture). Representative file: `tests/KoalaBooks.Tests/Api/ApiTests.cs` (532 lines) — `IAsyncLifetime` seeding pattern, `GetBearerTokenAsync()`/`AuthenticatedClientAsync()` helpers, `SeedSecondTenantAsync()` for cross-tenant 404 tests. Known gotcha (documented at `ApiTests.cs:472-483`): service calls that depend on `ICurrentUser`/query filters can't be invoked directly from a manually-created `IServiceScope` in test setup (no ambient `HttpContext` → filters exclude everything) — use `db.Set<T>().IgnoreQueryFilters()` for out-of-band seeding instead.

### 1.7 OpenAPI
Built-in `Microsoft.AspNetCore.OpenApi` (`AddOpenApi()`/`MapOpenApi()` in `Program.cs`), Scalar UI in Development only. No XML doc comments, no Swashbuckle/NSwag, no checked-in spec file — metadata comes entirely from `[ProducesResponseType]` + DTO shape. Nothing to "keep in sync" beyond the attributes themselves.

### 1.8 Known in-flight work — do not duplicate
- **PR #299** (preview-reversal) and **PR #302** (Agent G / Reports) are **merged to `main`** as of 2026-07-19. No longer a concern.
- **PR #291 merged to `main` on 2026-07-19** (with #298's interface relocation folded in). The ~14 Application-layer service interfaces listed in section 1.3 now live in `KoalaBooks.Domain.Interfaces` (confirmed by listing the directory post-merge), and the five existing controllers already reference the new namespace. **The prerequisite blocking Agent B-I is cleared** — this plan's branch has been merged with current `main` and builds clean (0 errors). Section 1.3's table above still lists interfaces under their old `Application` locations in a few spots; treat `KoalaBooks.Domain.Interfaces` as authoritative for all 14 relocated interfaces going forward.
- **PR #303** (issue #283, fiscal-year resolution) has landed a foundation PR (`GetForDateAsync`/`GetDefaultFiscalYearAsync`/`GetOpenFiscalYearsAsync` added, per-page fiscal-year selectors on SupplierInvoices/BankImport/CustomerInvoices/Accounts) — **`GetActiveAsync()` is still in `IFiscalYearService` today**, not yet deleted; the full plan (org-wide Todo/Review/Inbox, `GetActiveAsync` removal) is tracked separately and still pending. Agent B must re-check `IFiscalYearService`/`FiscalYearsController.cs` state on `main` before starting — this is actively evolving independently of #122.
  **Not a blocker for dispatching Agent B** — none of Agent B's endpoints (create, get-accounts-for-year, propagate-balances) touch `GetActiveAsync`, and #283's remaining scope (the removal) has no branch/PR yet. But it *is* a forward-looking risk: issue #122's endpoint table marks "FiscalYears GET active" ✅ (PR #273), backed by `IFiscalYearService.GetActiveAsync`, which the existing `FiscalYearsController.GetActive()` action calls directly. Whenever #283's remainder eventually removes `GetActiveAsync()`, that PR (not Agent B, not Agent I) must also update `FiscalYearsController.GetActive()` to call `GetDefaultFiscalYearAsync` (or equivalent) instead of deleting the backing method out from under a live, already-shipped endpoint.

### 1.9 Background-job status envelope (exists, mostly unused)
PR #285 built a generic async-job status system that already anticipates this program's needs: `BackgroundJobRun` (`KoalaBooks.Domain.Entities`) + `IBackgroundJobRunService` (`KoalaBooks.Application.Services`) + `BackgroundJobType` enum. The enum already reserves `ZipImport = 0` (the only one implemented so far, driving `Inbox.razor`), `SieImport = 1`, `BasImport = 2`, `YearEndClose = 3`, `SieExport = 4` — all unused today, clearly reserved for exactly the streams in this program and #279-282.

Pattern to copy for any new async job (confirmed via `ZipImportJob`/`HangfireZipImportQueue` in `src/KoalaBooks.Application/Jobs/`): DI-register a scoped `{Noun}Job` deriving `BackgroundJobRunBase` + an `IHangfire{Noun}Queue` wrapper; the job's entry point calls `LoadRunAsync(runId, jobId)`, does its work incrementally with `SaveProgressAsync(processedCount)` per unit (so a Hangfire retry resumes rather than restarts), and finishes with `CompleteAsync(status, resultPayload)`.

**Gap:** `IBackgroundJobRunService` currently only has `CreateRunAsync`, `GetOpenRunsAsync`, `AcknowledgeAsync` — **no single-run lookup by id**. Any REST status-poll endpoint (Agent H's SIE import, Agent F's upload-zip) needs a `GetByIdAsync(int runId)` (tenant-scoped) added first. Small, mechanical addition — not a design question. Whichever of Agent H or Agent F lands first should add it and the other should reuse it, rather than each adding their own.

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
| | ~~POST close year~~ | resolved | not a FiscalYearsController action — see **Year-end closing** row below (5.B) |
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
| **Customers** | list/by-id/create/update/deactivate | ⬜ (new controller) | `ICustomerService` — confirmed no `GetByIdAsync` (5.E resolved); Agent E adds it (trivial, org-scoped) before building the controller |
| **Customer invoices** | list/by-id/create/post/mark-paid/find-matching/delete/pdf (from-entry deferred, 5.E resolved) | ⬜ (new controller) | `ICustomerInvoiceService` + `CustomerInvoicePdfGenerator.Generate` — full scope implemented; `CreateFromEntryAsync` intentionally out of scope, tracked as a follow-up |
| **Documents** | pending list+count/upload/upload-zip/linked/link/metadata/delete/download | ⬜ (new controller) | `IDocumentService` — every method already exists |
| **Organisation profile** | GET current / PUT update | ⬜ (new controller, smallest stream) | `IOrganisationService` |
| **SIE import/export** | parse+preview/import-all (async via Hangfire)/export | ⬜ 🧩 | resolved (5.H-1): Agent H builds `SieImportJob`+`HangfireSieImportQueue` wrapping `ISieImportService`, reusing the `BackgroundJobRun` envelope (1.9), exposed via a REST status-poll endpoint. Subsumes #279 — close it as superseded once merged. `ISieExportService` unaffected, no gap. |
| **Account mapping** | build-mapping/apply-mapping | ⬜ | `IAccountMappingService` |
| **Year-end closing** | validate/preview/execute, nested under `fiscal-years/{id}/year-end-closing/...` (also satisfies the issue's FiscalYears "close year" bullet — 5.B) | ⬜ | `IYearEndClosingService` (`ValidateForClosingAsync`/`PreviewClosingAsync`/`ExecuteClosingAsync`, all three exposed for a future checks-based closing wizard) |
| **Voucher gaps** | gaps/explanations/add-explanation | ⬜ | `IVoucherGapService` |
| **Reports** | dashboard/balance-sheet/income-statement/trial-balance/general-ledger (3 sub-endpoints)/vat-report | ⬜ (new controller) | `IJournalEntryReportingService` — every method already exists |
| **Bulk journal import** | POST batch | ⬜ 🧩 | `IBulkJournalImportService` does not exist — resolved (5.H-2): all-or-nothing transactional semantics; method signature/DTO shape still needs a short design pass before controller code |
| **Infra** | cursor-based pagination | ⬜ (not assigned — cross-cutting, out of scope for lettered streams) | — |

---

## 3. Dependency Map

```
Infra/Auth (done: OpenIddict API client #120, PR #272)
   │
   ├── Agent B: Accounts + FiscalYears completion  — no dependency on other streams
   │        (5.B resolved: "close year" is entirely Agent H's Year-end-closing scope, not Agent B's)
   │
   ├── Agent C: Supplier invoices remaining verbs   — independent; touches only SupplierInvoicesController.cs
   │
   ├── Agent D: Bank transaction/import endpoints   — independent; touches only BankTransactionsController.cs
   │        (shares BankTransaction entity with Agent C's find-matching-bank-tx and Agent E's
   │         customer-invoice find-matching-bank-tx — same read-only method on 3 services, no write conflict)
   │
   ├── Agent E: Customers + Customer invoices        — new controllers; independent
   │        (5.E resolved: add ICustomerService.GetByIdAsync; customer-invoice from-entry deferred to a follow-up)
   │
   ├── Agent F: Documents                             — new controller; independent
   │        (upload-zip's background-job handle reuses the BackgroundJobRun status-poll endpoint
   │         Agent H builds for SIE import, see 1.9/5.H-1 — don't build a second one)
   │
   ├── Agent G: Reports                               — new controller, read-only; fully independent, zero service gaps
   │
   ├── Agent H: SIE import/export, account mapping,   — mixed independence:
   │            year-end closing, voucher gaps,          - account mapping / voucher gaps: independent, services exist
   │            bulk journal import                      - year-end closing: independent, services exist (5.B resolved — validate/preview/execute triad)
   │                                                      - SIE import: unblocked (5.H-1 resolved) — build the Hangfire wrapper now, subsumes #279
   │                                                      - bulk journal import: semantics resolved (5.H-2, all-or-nothing); still needs a short design pass
   │
   └── Organisation profile (unassigned in the A-I split above — smallest stream,
            fold into Agent B or Agent E, whichever finishes first, or hand to Agent A)

Agent A: conventions/DTO consistency/OpenAPI review — runs continuously alongside B-H,
         not sequentially before them (there's no new convention being introduced, just
         auditing each stream's PR against section 1 as they land)

Agent I: final audit — strictly after all of B-H (and A's last pass) land
```

No stream in B-H has a hard build-order dependency on another **except**:
- Bulk journal import (Agent H) — needs a short service-design pass (all-or-nothing transactional, per 5.H-2) before any controller code.
- Agent F's upload-zip status-poll endpoint and Agent H's SIE-import status-poll endpoint both need `IBackgroundJobRunService.GetByIdAsync` (1.9) — whichever lands first adds it, the other reuses it. Not a hard order, just coordinate so it's not added twice.

---

## 4. Recommended Execution Order

**Prerequisite cleared 2026-07-19: PR #291 merged to `main`.** #291 carried #298's interface relocation (`KoalaBooks.Application.Services` → `KoalaBooks.Domain.Interfaces` for ~14 interfaces) plus `using`-statement updates to all five existing controllers — see 1.8. This plan's branch has been merged with `main` post-#291 and builds clean. Agent B is now unblocked and next up per the order below.

1. **Agent G — Reports** — already done (PR #302, merged), ahead of this prerequisite being identified. No action needed.
2. **Agent B — Accounts + FiscalYears completion**: zero gaps, no open questions (5.B resolved — "close year" belongs entirely to Agent H's Year-end closing stream, not Agent B). **Next stream to dispatch.**
3. **Agent C — Supplier invoices remaining verbs** and **Agent D — Bank transaction/import endpoints** in parallel: both independent, both just wiring existing service methods into existing controllers, no new controllers.
4. **Agent E — Customers + Customer invoices** and **Agent F — Documents**: both new controllers. 5.E resolved (add `ICustomerService.GetByIdAsync`, defer customer-invoice from-entry to a follow-up). Agent F's upload-zip status endpoint should reuse `IBackgroundJobRunService.GetByIdAsync` (1.9) — coordinate with Agent H rather than adding it twice.
5. **Agent H — SIE import/export, account mapping, year-end closing, voucher gaps, bulk journal import**: split internally — ship account-mapping/voucher-gaps first (no gaps), then year-end closing (5.B: validate/preview/execute triad nested under fiscal-years), then SIE import (5.H-1: build the async Hangfire wrapper now — `SieImportJob`/`HangfireSieImportQueue`, add `IBackgroundJobRunService.GetByIdAsync`, expose a status-poll endpoint; subsumes #279, close it as superseded once merged), then bulk journal import last (5.H-2: design `IBulkJournalImportService` with all-or-nothing transactional semantics before any controller code).
6. **Organisation profile**: smallest stream, slot in wherever capacity opens (suggest folding into Agent B's PR or giving it to Agent E once Customers lands).
7. **Agent A — conventions/DTO/OpenAPI review**: not a phase, an ongoing pass — review each PR from 2-7 as it's opened, flag drift from section 1's patterns.
8. **Agent I — final audit**: only after every stream above has merged.

Agent A and Agent I are advisory/review roles, not implementation streams — they don't produce their own PRs, they annotate others'.

---

## 5. Sub-agent Assignment Plan

Each entry: scope, exact files, services consumed (all confirmed to exist unless flagged), and — where the stream had an open question — the resolution reached with the user on 2026-07-19.

### Agent A — API conventions, DTO consistency, OpenAPI review
**Role:** Review-only, continuous. No files of its own. Checklist per incoming PR: routes under `/api/v1/`, `[ProducesResponseType]` present including error codes, DTO naming (`{Noun}Response`/`Create{Noun}Request`) matches section 1.2, manual-mapping-only (no AutoMapper sneaking in), tenant-check present for any entity without a query filter, no named auth policies introduced.

### Agent B — AccountsController + FiscalYearsController completion
**Files:** modify `src/KoalaBooks.Web/Controllers/Api/AccountsController.cs`, `FiscalYearsController.cs`; new DTOs in `Models/Api/` (`CreateAccountRequest`, `UpdateAccountRequest`, `CopyAccountsRequest`, `CreateFiscalYearRequest`); tests in `tests/KoalaBooks.Tests/Api/ApiTests.cs` (or a split file if it's grown too large — check current line count first).
**Services:** `IAccountService` (all methods exist), `IFiscalYearService` (all methods exist).
**Resolved (5.B):** "close year" is **not** a FiscalYearsController action. `IYearEndClosingService` has `ValidateForClosingAsync`/`PreviewClosingAsync`/`ExecuteClosingAsync` — a validate → preview → execute triad, clearly built for a future checks-based closing wizard, not a single flat action. All three are owned by Agent H, exposed nested under `/api/v1/fiscal-years/{id}/year-end-closing/...` for discoverability (matches `FiscalYears.razor`, the only existing UI consumer, which already treats closing as a fiscal-year action). Agent B's scope is Accounts + the remaining FiscalYears verbs only (create, get-accounts-for-year, propagate-balances) — no closing-related files or DTOs.

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
**Resolved (5.E):** `ICustomerService` has no `GetByIdAsync` — only `GetAllAsync(int organisationId)`. Agent E adds a straightforward org-scoped `GetByIdAsync` to `ICustomerService`/`CustomerService` before building `CustomersController` — no design ambiguity, just an addition (in the service, not the controller). `ICustomerInvoiceService` does have a fiscal-year-scoped list method (`GetAllAsync(int fiscalYearId)`) and `GetByIdAsync`, confirmed working as-is. `CreateFromEntryAsync` (unlike `ISupplierInvoiceService`, which has one) is a genuine gap but **deferred to a follow-up issue** — out of scope here. Ship list/by-id/create/post/mark-paid/find-matching/delete/pdf only.

### Agent F — Documents/file handling endpoints
**Files:** new `DocumentsController.cs`; new DTOs (`DocumentResponse`, `LinkDocumentRequest`, `UpdateDocumentMetadataRequest`); tests (multipart upload test pattern will be new to this test suite — check no existing precedent in `ApiTests.cs` first).
**Services:** `IDocumentService` — every method (`UploadAsync`, `UpdateMetadataAsync`, `GetPendingAsync`/`GetPendingCountAsync`, `GetLinkedAsync`, `GetDownloadAsync`, `DeleteAsync`, `LinkAsync`, `UploadAndLinkAsync`, `UploadZipAsync`) confirmed to exist.
**Resolved:** `UploadZipAsync` returns `(int? RunId, string? Error)` — a `BackgroundJobRun` handle (ties into the poller infra from PR #285/#296). Reuse the REST status-poll endpoint Agent H builds for SIE import (5.H-1) rather than inventing a second one — both read the same `BackgroundJobRun` row via `IBackgroundJobRunService.GetByIdAsync` (see 1.9). If Agent F lands first, add `GetByIdAsync` itself and Agent H reuses it; coordinate whichever order they land in.

### Agent G — Reports endpoints
**Files:** new `ReportsController.cs`; new DTOs for each report shape (check `IJournalEntryReportingService` return types before naming — several likely already have suitable shapes without needing new response records); tests.
**Services:** `IJournalEntryReportingService` — every method (`GetDashboardStatsAsync`, `GetBalanceSheetAsync`, `GetIncomeStatementAsync`, `GetTrialBalanceAsync`, `GetGeneralLedgerAsync`, `GetComputedBalancesAsync`, `GetAccountIdsWithTransactionsAsync`, `GetAccountLedgerAsync`, `GetVatReportAsync`) confirmed to exist. `IVatReportCsvExporter` if a CSV variant is wanted.
**No open questions — cleanest stream, recommended to go first (section 4).**

### Agent H — SIE import/export, account mapping, year-end closing, voucher gaps, bulk journal import
**Files:** new `SieController.cs` (or split import/export), `AccountMappingController.cs`, `YearEndClosingController.cs`, `VoucherGapsController.cs`; a new `IBulkJournalImportService`/`BulkJournalImportService` pair in `src/KoalaBooks.Application/Services/` plus a controller action for it; tests for each.
**Services:** `IAccountMappingService`, `IYearEndClosingService`, `IVoucherGapService` — all fully implemented, no gaps, safe to build immediately. `ISieImportService`/`ISieExportService` exist; see 5.H-1 for the async wrapper Agent H builds around `ISieImportService`. `IBulkJournalImportService` doesn't exist at all — see 5.H-2.
**Resolved (5.H-1, SIE):** #279 (Hangfire-based async SIE import) has no branch or PR yet — unstarted. PR #285's shared background-job infra already reserves `BackgroundJobType.SieImport` for exactly this. **Do not build a synchronous "upload, wait, get result" SIE import endpoint** — instead, build the async wrapper now, subsuming #279:
- Add `SieImportJob` + `HangfireSieImportQueue` under `src/KoalaBooks.Application/Jobs/`, mirroring `ZipImportJob`/`HangfireZipImportQueue` exactly (wrap `ISieImportService.ImportAllAsync`/`ImportFiscalYearAsync`, drive it through `BackgroundJobRunBase`, DI-register in `Program.cs` next to the zip-import registrations).
- Add `IBackgroundJobRunService.GetByIdAsync(int runId)` (doesn't exist yet — see 1.9), tenant-scoped, needed for the status-poll endpoint below and shared with Agent F's upload-zip.
- Expose `POST .../sie/import` (enqueues, returns the `RunId`) + a `GET` status-poll endpoint reading `BackgroundJobRun` by id.
- Once merged, close #279 as superseded — same pattern as #169's split into #279-282.

**Resolved (5.H-2, bulk import):** all-or-nothing transactional semantics — a single DB transaction across the whole batch; any invalid entry rolls back the entire import. Deliberately different from the `SieImportAllResult`/`ZipImportResult` partial-success-with-warnings convention used elsewhere in the codebase, because this is a direct financial write, not a document/reference-data import. The semantics are settled but the exact shape isn't — still write a short interface-design note (method signature, request/response DTO shape, validate-before-transaction vs fail-mid-transaction-and-rollback) before any controller code, per the "identify gaps, don't bypass the architecture" rule.

### Agent I — Final audit
**Role:** after B-H (and Organisation profile, wherever it lands) are merged — compare final endpoint set against section 2's table, confirm every ⬜ became ✅, confirm every new controller has integration tests covering happy-path/401/404-cross-tenant, confirm every action has `[ProducesResponseType]`, confirm `/openapi/v1.json` reflects every new route (`curl` it and diff against the endpoint table). Produces a gap report, not code.

---

## Self-review notes
- Spec coverage: every checklist item in issue #122's body maps to a row in section 2 and an agent in section 5, except the standalone "cursor-based pagination" infra item, which is explicitly called out as unassigned/out-of-scope for this pass (matches "work incrementally" — it's a cross-cutting change to an already-shipped endpoint, not new coverage).
- Four genuine ambiguities were found by reading the code rather than assumed away: FiscalYears "close year" vs Year-end closing overlap (5.B), `ICustomerService`'s missing `GetByIdAsync` and `ICustomerInvoiceService`'s missing `CreateFromEntryAsync` (5.E), and the SIE-import/#279 async-shape cross-reference plus the missing `IBulkJournalImportService` (5.H). All four were resolved with the user on 2026-07-19 rather than invented — see each section's "Resolved" note. Decisions: Year-end closing becomes its own resource (validate/preview/execute triad) nested under `fiscal-years/{id}/...`, owned by Agent H, not a separate FiscalYears action; customer-invoice from-entry deferred to a follow-up issue; SIE import gets an async Hangfire wrapper built now, reusing PR #285's `BackgroundJobRun` envelope and subsuming #279; bulk journal import uses all-or-nothing transactional semantics.
- PR #299 status (open, not merged) was verified via `gh pr view`, not assumed from the issue checklist — the checklist alone would have led every stream to falsely believe `JournalEntriesController.cs` was fully done.
