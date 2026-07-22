# Issue #290: bearer-authed download plumbing for PDF/SIE/VAT-report exports

**Spec date:** 2026-07-21
**Closes:** #290 (WASM download plumbing gap), and folds in issue #122's "Agent E" stream (Customers + Customer invoices REST API), since #290's PDF endpoint needs `CustomerInvoicesController` to exist.

## Background

WASM rendering (`@rendermode InteractiveAuto`) is live for one page (`Review.razor`, via PR #291), so #290's own precondition ("actionable once WASM is turned on for at least one page") is now met. Three byte-producing downloads in the app still depend on either a live Blazor Server circuit or ASP.NET Identity cookie auth, neither of which a WASM-hosted page has:

- SIE export (`SieExport.razor`) — bytes generated in-process by `ISieExportService`, pushed to the browser as base64 over `JS.InvokeVoidAsync`.
- VAT report CSV export (`VatReport.razor`) — same base64-push pattern, but the byte-generation itself (`IVatReportCsvExporter.Build`) is a pure function with no DB dependency.
- Customer invoice PDF (`CustomerInvoices.razor` → `/customer-invoices/{id}/pdf`) — a minimal-API route authenticated via the Identity cookie rather than OpenIddict bearer auth, the only such route among API-shaped endpoints in the app.

No REST endpoint exists today for SIE export or customer-invoice PDF. No `CustomersController`/`CustomerInvoicesController` exists at all — issue #122's program plan already reserves an "Agent E" stream for exactly that, not yet dispatched. Building the PDF endpoint here means building that controller now instead of later.

## Decisions (confirmed with user, 2026-07-21)

1. **Agent E scope folded in.** Build the full `CustomersController` + `CustomerInvoicesController` (list/by-id/create/update/deactivate for customers; list/by-id/create/post/mark-paid/find-matching/delete/pdf for invoices) now, not just the `pdf` action. `CreateFromEntryAsync` stays deferred (per the #122 plan — a genuine service-layer gap, out of scope).
2. **Download delivery unifies across render modes.** One code path for both Blazor Server and future WASM rendering — no dual base64-vs-Blob implementations. The existing per-interface DI-swap pattern (`IJournalEntryService` → `JournalEntryApiService` under WASM) already abstracts *where* the bytes come from; this spec only needs to fix *how* bytes reach the browser once available, and do it the same way regardless of render mode.
3. **No render-mode flip in this PR.** `SieExport.razor`, `VatReport.razor`, `CustomerInvoices.razor` stay Server-rendered. This PR only builds the REST endpoints, client-side (WASM) service implementations, and the new download JS interop — ready for a future page-by-page `InteractiveAuto` migration, matching how `Review.razor` got its own dedicated PoC (#256) rather than a blanket flip.
4. **Customer invoice PDF keeps its "open in new tab" UX**, now via a Blob URL instead of a plain authenticated link.

## Design

### 1. Layering fix (prerequisite)

`CustomerInvoicePdfGenerator` (currently `KoalaBooks.Web.Services`) only depends on `Domain.Entities` and QuestPDF — no Web-specific dependency. Move it to `KoalaBooks.Application.Services`, mirroring `VatReportCsvExporter` (already there, same shape: pure function over already-loaded domain data). This lets `CustomerInvoiceService` (Application layer) call it directly without violating `Domain <- Infrastructure <- Application <- Web` layering.

### 2. Service interface additions

- `ICustomerService.GetByIdAsync(int id)` → `Customer?`, org-scoped. Missing today (confirmed by #122 plan section 5.E); trivial addition alongside the existing `GetAllAsync(int organisationId)`.
- `ICustomerInvoiceService.GetPdfAsync(int id)` → `byte[]?`. Server (`CustomerInvoiceService`) implementation: `GetByIdAsync(id)` (with `.Lines` and `.FiscalYear.Organisation` loaded, matching what the current minimal-API route needs) then `CustomerInvoicePdfGenerator.Generate(invoice)`; returns `null` if the invoice doesn't exist.
- `ISieExportService` — unchanged; `ExportAsync(int fiscalYearId, string? companyName)` already exists and already needs no new capability.

### 3. New REST controllers

All under `api/v1`, all following the existing pattern (`[ApiController]`, `[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]`, `[Route("api/v1")]`, `[ProducesResponseType]` on every action including 401/404, constructor-DI of interfaces only, DTOs in `KoalaBooks.Web.Models.Api`, no business logic in the controller) — see `SupplierInvoicesController`/`ReportsController` as the reference shape.

- **`CustomersController`** — no existing controller injects `ICurrentUser` directly (fiscal-year-scoped controllers rely on the tenant-filtered `FiscalYear` lookup instead), but `Customer` is org-scoped directly, not via a fiscal year, and `ICustomerService.GetAllAsync` takes an explicit `organisationId` — matching `Customers.razor`'s own `CurrentUser.OrganisationId ?? throw ...` pattern, `CustomersController` injects `ICurrentUser` and resolves the org id the same way (the `Customer` entity's global query filter is a second, redundant safety net either way, same as other tenant-scoped entities per `AppDbContext`).
  - `GET api/v1/customers` → list, org resolved from `ICurrentUser.OrganisationId`
  - `GET api/v1/customers/{id}` → by id, 404 if missing or cross-tenant
  - `POST api/v1/customers` → create
  - `PUT api/v1/customers/{id}` → update
  - `POST api/v1/customers/{id}/deactivate` → deactivate

- **`CustomerInvoicesController`**
  - `GET api/v1/fiscal-years/{fiscalYearId}/customer-invoices` → list (paged, matching `SupplierInvoicesController`'s `PagedResult<T>` convention)
  - `GET api/v1/customer-invoices/{id}` → by id
  - `POST api/v1/customer-invoices` → create
  - `POST api/v1/customer-invoices/{id}/post` → post to ledger
  - `POST api/v1/customer-invoices/{id}/mark-paid` → mark paid
  - `GET api/v1/fiscal-years/{fiscalYearId}/customer-invoices/find-matching-bank-tx` → find matching bank transactions
  - `DELETE api/v1/customer-invoices/{id}` → delete
  - `GET api/v1/customer-invoices/{id}/pdf` → `FileContentResult`, `application/pdf`, 404 if missing

- **`SieController`** (new, minimal — export only; import stays with #122's Agent H Hangfire-backed stream, untouched here)
  - `GET api/v1/fiscal-years/{fiscalYearId}/sie-export?companyName=` → `FileContentResult`, `application/octet-stream`

### 4. WASM-side client implementations

New files in `KoalaBooks.Client/Services`, same shape as `AccountApiService`/`SupplierInvoiceApiService` (HTTP-backed via the existing bearer-authed `"KoalaBooks.Api"` named `HttpClient`, `ApiJson.Options` for deserialization, `Task.FromException` for any interface member with no backing endpoint):

- `CustomerApiService : ICustomerService`
- `CustomerInvoiceApiService : ICustomerInvoiceService`
- `SieExportApiService : ISieExportService`

`IVatReportCsvExporter` needs no API variant — it's pure, so `KoalaBooks.Client/Program.cs` registers the real `VatReportCsvExporter` class directly (no interface swap needed, same concrete type Server uses).

All four registrations added to `KoalaBooks.Client/Program.cs` alongside the existing PoC-scope block.

### 5. Download delivery — unified stream-based JS interop

Retire `downloadFileFromBase64` in `download.js`. Replace with a stream-based helper using Blazor's `DotNetStreamReference`, which works identically under Server (tunneled over the circuit) and WASM (direct in-browser interop) — avoiding the ~33% base64 size penalty for what are otherwise ordinary byte-array downloads.

- New JS function, e.g. `window.koala.downloadFileFromStream = async (streamRef, fileName, contentType) => { ... }`: reads the stream into a `Blob`, creates an object URL, and either (a) clicks a synthetic `<a download>` anchor (SIE export, VAT CSV) or (b) `window.open()`s it (customer invoice PDF), then revokes the object URL.
- C# call sites (`SieExport.razor`, `VatReport.razor`, `CustomerInvoices.razor`) wrap their `byte[]` result in a `DotNetStreamReference` and call the JS helper via `IJSRuntime`.
- `CustomerInvoices.razor`'s `<a href="/customer-invoices/{id}/pdf" target="_blank">` becomes a button that calls `ICustomerInvoiceService.GetPdfAsync(id)` then the JS helper.
- The old cookie-authed `app.MapGet("/customer-invoices/{id:int}/pdf", ...)` minimal-API route in `Program.cs` is deleted.
- `window.print` (`VatReport.razor`) is untouched — built-in dotted-path JS interop, no custom wrapper, not part of this gap.

### 6. Testing

Integration tests via `WebApiFactory` + Testcontainers Postgres for every new controller action: happy path, 401 without token, cross-tenant 404 where the resource is tenant-scoped — matching the #122 program plan's global constraint. Any existing test referencing `KoalaBooks.Web.Services.CustomerInvoicePdfGenerator` gets its `using`/namespace updated for the move to `KoalaBooks.Application.Services`.

## Out of scope

- `/documents/{id}` has the identical cookie-auth gap (same pattern as the old PDF route) but isn't named in #290 — left alone. Worth its own follow-up issue if/when a document-consuming page goes WASM.
- SIE *import* (Agent H's Hangfire-backed stream, per the #122 plan) — untouched.
- `CreateFromEntryAsync` for customer invoices — deferred, per #122 plan 5.E.
- Flipping `SieExport.razor`/`VatReport.razor`/`CustomerInvoices.razor` to `@rendermode InteractiveAuto` — future, page-by-page work.
