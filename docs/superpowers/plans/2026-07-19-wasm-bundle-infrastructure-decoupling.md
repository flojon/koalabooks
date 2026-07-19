# WASM Bundle Infrastructure Decoupling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the 14 use-case service interfaces (plus one static helper) that `KoalaBooks.Components` pages inject from `KoalaBooks.Application.Services` into `KoalaBooks.Domain.Interfaces`, then swap `KoalaBooks.Components`'s `ProjectReference` from `KoalaBooks.Application` to `KoalaBooks.Domain` — so the WASM client's build graph structurally excludes the entire `Application → Infrastructure` chain (Npgsql, EF Core, PdfPig, ExcelDataReader, OpenIddict, Identity, DataProtection).

**Architecture:** This is a mechanical, non-behavioral namespace relocation, not a redesign. Concrete EF-backed service implementations (`AccountService`, `JournalEntryService`, etc.) stay exactly where they are in `KoalaBooks.Application.Services`; only their interface declarations (and any DTOs/records co-located in the same file) move to `KoalaBooks.Domain.Interfaces`. Every consumer (`Components` razor files, `Web` controllers, `Client` API services, `Tests`) gets its `using`/`@using` updated to point at the new namespace. No routing, render-mode, or DI-registration logic changes anywhere. The design doc (`docs/superpowers/specs/2026-07-19-wasm-bundle-infrastructure-decoupling-design.md`) has full rationale; read it if anything here is ambiguous about *why*.

**Tech Stack:** .NET 10 / C#, Blazor (Server + WASM `InteractiveAuto`), no new package dependencies (every moved interface only touches `Domain.Entities`/`Domain.Enums`/BCL types — already verified in the design doc).

## Global Constraints

- No new NuGet package references are needed in `KoalaBooks.Domain` for this work (the one interface that did need one, `ISieImportService`/`jsisie`, was already added in a prior investigation step — see Task 1).
- Concrete service implementation classes (e.g. `AccountService : IAccountService`) stay in `KoalaBooks.Application.Services` and keep using `KoalaBooks.Infrastructure.Data.AppDbContext` — only interface/DTO declarations move.
- `JournalEntryExtensions` and `DemoDataSeeder` (also in `Application.Services`) are untouched — verified neither references any of the moved types.
- DI registrations in `KoalaBooks.Web/Program.cs` (`AddScoped<IFiscalYearService, FiscalYearService>()`, etc.) are unchanged in behavior — `Program.cs` already imports both `KoalaBooks.Application.Services` and `KoalaBooks.Domain.Interfaces`, so no edit is needed there at all.
- Follow the exact pattern already established by `ISieExportService`, `IBankImportService`, and the (currently uncommitted) `ISieImportService` move: interface + co-located DTOs in one file under `KoalaBooks.Domain.Interfaces`, namespace-only, no behavior change.
- Build the solution via `dotnet build` / `dotnet test` from the repo root — `KoalaBooks.slnx` is picked up automatically (there is no `.sln`).
- Several tasks below leave the full solution in a deliberately non-compiling intermediate state (e.g. `Web`/`Components`/`Client` won't build until their own task lands) — each task's verification step says exactly which project(s) are expected to build cleanly at that point. Don't be alarmed by unrelated project errors until the task that fixes them.

---

## Task 1: Commit the already-completed foundational move

A prior investigation session already did the first slice of this work as a proof of concept — moved `ISieImportService` (and its co-located DTOs `SieImportPreview`, `SieImportFiscalYear`, `SieImportResult`, `SieImportAllResult`) from `KoalaBooks.Infrastructure.Services` to `KoalaBooks.Domain.Interfaces`, added the `jsisie` package reference to `KoalaBooks.Domain.csproj` it needed, and cleaned up 4 dead `@using KoalaBooks.Infrastructure.Services` statements in `Accounts.razor`, `FiscalYears.razor`, `SieExport.razor`, `SieImport.razor`. This is currently sitting uncommitted in the working tree and establishes the exact pattern every later task follows. Commit it as its own logical unit before starting new work, along with the design spec this plan implements.

**Files:**
- Already modified (uncommitted): `src/KoalaBooks.Components/KoalaBooks.Components.csproj`, `src/KoalaBooks.Components/Pages/Accounts.razor`, `src/KoalaBooks.Components/Pages/FiscalYears.razor`, `src/KoalaBooks.Components/Pages/SieExport.razor`, `src/KoalaBooks.Components/Pages/SieImport.razor`, `src/KoalaBooks.Domain/KoalaBooks.Domain.csproj`, `src/KoalaBooks.Infrastructure/Services/SieImportService.cs`
- Already deleted (uncommitted): `src/KoalaBooks.Infrastructure/Services/ISieImportService.cs`
- Already created (untracked): `src/KoalaBooks.Domain/Interfaces/ISieImportService.cs`
- Add: `docs/superpowers/specs/2026-07-19-wasm-bundle-infrastructure-decoupling-design.md` (untracked design doc)
- Add: `docs/superpowers/plans/2026-07-19-wasm-bundle-infrastructure-decoupling.md` (this plan)

**Interfaces:**
- Produces: the reference pattern every later task copies — `namespace KoalaBooks.Domain.Interfaces;`, co-located DTOs first, interface declaration last, in one file named after the interface.

- [ ] **Step 1: Review the pending diff**

```bash
git status
git diff -- src/KoalaBooks.Components/KoalaBooks.Components.csproj src/KoalaBooks.Domain/KoalaBooks.Domain.csproj src/KoalaBooks.Infrastructure/Services/SieImportService.cs src/KoalaBooks.Components/Pages/Accounts.razor src/KoalaBooks.Components/Pages/FiscalYears.razor src/KoalaBooks.Components/Pages/SieExport.razor src/KoalaBooks.Components/Pages/SieImport.razor
```

Confirm the diff matches the description above: `ISieImportService` interface + 4 DTOs moved out of `SieImportService.cs` into `src/KoalaBooks.Domain/Interfaces/ISieImportService.cs`, `jsisie` package added to `Domain.csproj`, `Components.csproj`'s `ProjectReference` to `KoalaBooks.Infrastructure` removed, and the 4 dead `@using KoalaBooks.Infrastructure.Services` lines removed from the listed `.razor` files.

- [ ] **Step 2: Build and test to confirm this baseline is green**

```bash
dotnet build
dotnet test
```

Expected: both succeed with no errors (this is already-working code, just uncommitted).

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Components/KoalaBooks.Components.csproj \
        src/KoalaBooks.Components/Pages/Accounts.razor \
        src/KoalaBooks.Components/Pages/FiscalYears.razor \
        src/KoalaBooks.Components/Pages/SieExport.razor \
        src/KoalaBooks.Components/Pages/SieImport.razor \
        src/KoalaBooks.Domain/KoalaBooks.Domain.csproj \
        src/KoalaBooks.Infrastructure/Services/ISieImportService.cs \
        src/KoalaBooks.Infrastructure/Services/SieImportService.cs \
        src/KoalaBooks.Domain/Interfaces/ISieImportService.cs \
        docs/superpowers/specs/2026-07-19-wasm-bundle-infrastructure-decoupling-design.md \
        docs/superpowers/plans/2026-07-19-wasm-bundle-infrastructure-decoupling.md
git commit -m "Move ISieImportService to Domain.Interfaces; add WASM bundle decoupling plan"
```

---

## Task 2: Move Batch A interfaces (no co-located DTOs) to Domain.Interfaces

Nine interfaces with no co-located DTOs move from `KoalaBooks.Application.Services` to `KoalaBooks.Domain.Interfaces`: `IAccountService`, `ICustomerInvoiceService`, `ICustomerService`, `IDocumentProvider`, `IFiscalYearService`, `IJournalEntryService`, `IOrganisationService`, `ISupplierInvoiceService`, `IVoucherGapService`. Each becomes its own file in `Domain/Interfaces/`, deleted from `Application/Services/`. The concrete implementation files need `using KoalaBooks.Domain.Interfaces;` added — except `FiscalYearService.cs` and `OrganisationService.cs`, which already have it (added for other reasons).

Consumers (`Components` razor files, `Web` controllers, `Client`) are **not** touched in this task — they still compile today only via `Components → Application → Domain` transitivity, which stays intact until Task 5's `ProjectReference` swap. This task's own verification is scoped to `Domain` + `Application` only; `Web`/`Components`/`Client` are expected to fail to build until Tasks 3, 5, and 6.

**Files:**
- Create: `src/KoalaBooks.Domain/Interfaces/IAccountService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/ICustomerInvoiceService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/ICustomerService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IDocumentProvider.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IFiscalYearService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IJournalEntryService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IOrganisationService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/ISupplierInvoiceService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IVoucherGapService.cs`
- Delete: `src/KoalaBooks.Application/Services/IAccountService.cs`, `ICustomerInvoiceService.cs`, `ICustomerService.cs`, `IDocumentProvider.cs`, `IFiscalYearService.cs`, `IJournalEntryService.cs`, `IOrganisationService.cs`, `ISupplierInvoiceService.cs`, `IVoucherGapService.cs`
- Modify: `src/KoalaBooks.Application/Services/AccountService.cs`, `CustomerInvoiceService.cs`, `CustomerService.cs`, `JournalEntryService.cs`, `SupplierInvoiceService.cs`, `VoucherGapService.cs` (add `using KoalaBooks.Domain.Interfaces;`)

**Interfaces:**
- Produces: `KoalaBooks.Domain.Interfaces.IAccountService`, `.ICustomerInvoiceService`, `.ICustomerService`, `.IDocumentProvider`, `.IFiscalYearService`, `.IJournalEntryService`, `.IOrganisationService`, `.ISupplierInvoiceService`, `.IVoucherGapService` — signatures unchanged from their current `Application.Services` versions.

- [ ] **Step 1: Create the 9 new Domain interface files**

`src/KoalaBooks.Domain/Interfaces/IAccountService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IAccountService
{
    Task<List<Account>> GetAllAsync(int fiscalYearId);
    Task<Account?> GetByIdAsync(int id);
    Task<Account> CreateAsync(Account account);
    Task UpdateAsync(Account account);
    Task ToggleActiveAsync(int id);
    Task<List<Account>> GetMissingFromSourceAsync(int currentFiscalYearId, int sourceFiscalYearId);
    Task<int> CopyAccountsAsync(int targetFiscalYearId, List<int> sourceAccountIds);
}
```

`src/KoalaBooks.Domain/Interfaces/ICustomerInvoiceService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface ICustomerInvoiceService
{
    Task<List<CustomerInvoice>> GetAllAsync(int fiscalYearId);
    Task<CustomerInvoice?> GetByIdAsync(int id);
    Task<(CustomerInvoice? Invoice, string? Error)> CreateAsync(
        CustomerInvoice invoice, List<CustomerInvoiceLine> lines);
    Task<(CustomerInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId,
        int receivableAccountId,
        int revenueAccountId,
        IReadOnlyDictionary<int, int> vatRateAccountIds);
    Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate);
    Task<(CustomerInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int receivableAccountId,
        int? linkBankTransactionId = null);
    Task<string?> DeleteAsync(int invoiceId);
    Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix);
}
```

`src/KoalaBooks.Domain/Interfaces/ICustomerService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface ICustomerService
{
    Task<List<Customer>> GetAllAsync(int organisationId);
    Task<(Customer? Customer, string? Error)> CreateAsync(Customer customer);
    Task<(Customer? Customer, string? Error)> UpdateAsync(Customer customer);
    Task<string?> DeactivateAsync(int customerId);
}
```

`src/KoalaBooks.Domain/Interfaces/IDocumentProvider.cs`:
```csharp
namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentProvider
{
    string GetDownloadUrl(int documentId);
}
```

`src/KoalaBooks.Domain/Interfaces/IFiscalYearService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IFiscalYearService
{
    Task<List<FiscalYear>> GetAllAsync();
    Task<FiscalYear?> GetByIdAsync(int id);
    Task<FiscalYear?> GetActiveAsync();
    Task<FiscalYear> CreateAsync(FiscalYear fiscalYear);
    Task<List<Account>> GetAccountsAsync(int fiscalYearId);
    Task PropagateBalancesToNextYearAsync(int fiscalYearId);
}
```

`src/KoalaBooks.Domain/Interfaces/IJournalEntryService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IJournalEntryService
{
    Task<List<JournalEntry>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null);
    Task<int> CountDraftsAsync(int fiscalYearId);
    Task<JournalEntry?> GetByIdAsync(int id);
    Task<(JournalEntry? Entry, string? Error)> CreateAsync(JournalEntry entry);
    Task<(JournalEntry? Entry, string? Error)> UpdateAsync(JournalEntry entry);
    Task<string?> PostAsync(int entryId);
    Task<string?> DeleteDraftAsync(int entryId);
    Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason);
    Task<(JournalEntry? Preview, string? Error)> PreviewReversalAsync(int entryId, string reason);
}
```

`src/KoalaBooks.Domain/Interfaces/IOrganisationService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IOrganisationService
{
    Task<Organisation?> GetCurrentAsync();
    Task<string?> UpdateAsync(string name, string? orgNumber);
}
```

`src/KoalaBooks.Domain/Interfaces/ISupplierInvoiceService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface ISupplierInvoiceService
{
    Task<int> CountUnpaidAsync(int fiscalYearId);
    Task<List<SupplierInvoice>> GetAllAsync(int fiscalYearId);
    Task<SupplierInvoice?> GetByIdAsync(int id);
    Task<(SupplierInvoice? Invoice, string? Error)> UpdateAsync(SupplierInvoice invoice);
    Task<(SupplierInvoice? Invoice, string? Error)> CreateAsync(SupplierInvoice invoice);
    Task<(SupplierInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int expenseAccountId, int payableAccountId, int? vatAccountId);
    Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate);
    Task<(SupplierInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int payableAccountId,
        int? linkBankTransactionId = null);
    Task<string?> DeleteAsync(int invoiceId);
    Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix);
    Task<List<string>> GetSuppliersAsync(int fiscalYearId);
    Task<HashSet<int>> GetLinkedJournalEntryIdsAsync(int fiscalYearId);
    Task<List<JournalEntry>> GetLinkableEntriesAsync(int fiscalYearId);
    Task<(SupplierInvoice? Invoice, string? Error)> CreateFromEntryAsync(
        int journalEntryId,
        SupplierInvoice invoice);
}
```

`src/KoalaBooks.Domain/Interfaces/IVoucherGapService.cs`:
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IVoucherGapService
{
    Task<List<int>> FindGapsAsync(int fiscalYearId);
    Task<List<int>> GetUnexplainedGapsAsync(int fiscalYearId);
    Task<string?> AddExplanationAsync(int fiscalYearId, int missingEntryNumber, string explanation, string explainedBy);
    Task<List<VoucherGapExplanation>> GetExplanationsAsync(int fiscalYearId);
}
```

- [ ] **Step 2: Delete the 9 old interface files from Application.Services**

```bash
git rm src/KoalaBooks.Application/Services/IAccountService.cs \
       src/KoalaBooks.Application/Services/ICustomerInvoiceService.cs \
       src/KoalaBooks.Application/Services/ICustomerService.cs \
       src/KoalaBooks.Application/Services/IDocumentProvider.cs \
       src/KoalaBooks.Application/Services/IFiscalYearService.cs \
       src/KoalaBooks.Application/Services/IJournalEntryService.cs \
       src/KoalaBooks.Application/Services/IOrganisationService.cs \
       src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs \
       src/KoalaBooks.Application/Services/IVoucherGapService.cs
```

- [ ] **Step 3: Add `using KoalaBooks.Domain.Interfaces;` to the concrete implementation files that don't already have it**

In `src/KoalaBooks.Application/Services/AccountService.cs`, change:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```
to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```

In `src/KoalaBooks.Application/Services/CustomerInvoiceService.cs`, change:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```
to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```

In `src/KoalaBooks.Application/Services/CustomerService.cs`, change:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```
to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```

In `src/KoalaBooks.Application/Services/JournalEntryService.cs`, change:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```
to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```
(This file also implements `IJournalEntryReportingService`, moved in Task 4 — this same `using` line covers that too, so Task 4 doesn't need to touch this file's usings again.)

In `src/KoalaBooks.Application/Services/SupplierInvoiceService.cs`, change:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```
to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```

In `src/KoalaBooks.Application/Services/VoucherGapService.cs`, change:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```
to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
```

`FiscalYearService.cs` and `OrganisationService.cs` already have `using KoalaBooks.Domain.Interfaces;` — no change needed to either.

- [ ] **Step 4: Verify Domain and Application build cleanly**

```bash
dotnet build src/KoalaBooks.Domain/KoalaBooks.Domain.csproj
dotnet build src/KoalaBooks.Application/KoalaBooks.Application.csproj
```

Expected: both succeed. (`dotnet build` on the whole solution will fail here — `Web`, `Components`, `Client`, `Tests` haven't been updated yet. That's expected until Tasks 3, 5, 6, 7.)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/IAccountService.cs \
        src/KoalaBooks.Domain/Interfaces/ICustomerInvoiceService.cs \
        src/KoalaBooks.Domain/Interfaces/ICustomerService.cs \
        src/KoalaBooks.Domain/Interfaces/IDocumentProvider.cs \
        src/KoalaBooks.Domain/Interfaces/IFiscalYearService.cs \
        src/KoalaBooks.Domain/Interfaces/IJournalEntryService.cs \
        src/KoalaBooks.Domain/Interfaces/IOrganisationService.cs \
        src/KoalaBooks.Domain/Interfaces/ISupplierInvoiceService.cs \
        src/KoalaBooks.Domain/Interfaces/IVoucherGapService.cs \
        src/KoalaBooks.Application/Services/AccountService.cs \
        src/KoalaBooks.Application/Services/CustomerInvoiceService.cs \
        src/KoalaBooks.Application/Services/CustomerService.cs \
        src/KoalaBooks.Application/Services/JournalEntryService.cs \
        src/KoalaBooks.Application/Services/SupplierInvoiceService.cs \
        src/KoalaBooks.Application/Services/VoucherGapService.cs
git commit -m "Move 9 DTO-free service interfaces from Application to Domain.Interfaces"
```

---

## Task 3: Fix Web layer usings for the interfaces moved in Task 2

Six files in `KoalaBooks.Web` reference the 9 interfaces moved in Task 2. `Program.cs` needs no change (already imports both namespaces). The other five each only reference moved interfaces and nothing else from `Application.Services`, so their `using KoalaBooks.Application.Services;` line is replaced outright (or, for `BankTransactionsController.cs`, simply deleted since it already imports `Domain.Interfaces`).

**Files:**
- Modify: `src/KoalaBooks.Web/Services/WebDocumentProvider.cs`
- Modify: `src/KoalaBooks.Web/Controllers/Api/AccountsController.cs`
- Modify: `src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs`
- Modify: `src/KoalaBooks.Web/Controllers/Api/SupplierInvoicesController.cs`
- Modify: `src/KoalaBooks.Web/Controllers/Api/FiscalYearsController.cs`
- Modify: `src/KoalaBooks.Web/Controllers/Api/BankTransactionsController.cs`

**Interfaces:**
- Consumes: `KoalaBooks.Domain.Interfaces.IDocumentProvider`, `.IAccountService`, `.IJournalEntryService`, `.ISupplierInvoiceService`, `.IFiscalYearService` from Task 2.

- [ ] **Step 1: Swap the using in the 5 files that only need a replace**

`src/KoalaBooks.Web/Services/WebDocumentProvider.cs` — change:
```csharp
using KoalaBooks.Application.Services;
```
to:
```csharp
using KoalaBooks.Domain.Interfaces;
```

`src/KoalaBooks.Web/Controllers/Api/AccountsController.cs` — change line 1 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

`src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs` — change line 1 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

`src/KoalaBooks.Web/Controllers/Api/SupplierInvoicesController.cs` — change line 1 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

`src/KoalaBooks.Web/Controllers/Api/FiscalYearsController.cs` — change line 1 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

- [ ] **Step 2: Remove the now-redundant using in BankTransactionsController.cs**

`src/KoalaBooks.Web/Controllers/Api/BankTransactionsController.cs` currently starts:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
```
Delete the first line (`using KoalaBooks.Application.Services;` — `Domain.Interfaces` is already imported and now covers everything this file needs):
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
```

- [ ] **Step 3: Verify Web builds cleanly**

```bash
dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj
```

Expected: succeeds. (`Components`, `Client`, `Tests` are still expected to fail until later tasks.)

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Web/Services/WebDocumentProvider.cs \
        src/KoalaBooks.Web/Controllers/Api/AccountsController.cs \
        src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs \
        src/KoalaBooks.Web/Controllers/Api/SupplierInvoicesController.cs \
        src/KoalaBooks.Web/Controllers/Api/FiscalYearsController.cs \
        src/KoalaBooks.Web/Controllers/Api/BankTransactionsController.cs
git commit -m "Fix Web layer usings for interfaces moved to Domain.Interfaces"
```

---

## Task 4: Move Batch B interfaces (with co-located DTOs) and VatQuarterHelper to Domain.Interfaces

Five interfaces that each carry co-located DTOs, plus the static `VatQuarterHelper` helper, move from `KoalaBooks.Application.Services` to `KoalaBooks.Domain.Interfaces`: `IAccountMappingService` (`MappingRow`, `ApplyMappingResult`), `IDocumentService` (`DocumentMeta`, `ZipImportResult`), `IJournalEntryReportingService` (11 report DTOs), `IVatReportCsvExporter` (depends on `VatReportData`, so must move after/alongside `IJournalEntryReportingService`), `IYearEndClosingService` (5 closing DTOs). After this task, every interface `Components` needs is out of `Application.Services`.

**Files:**
- Create: `src/KoalaBooks.Domain/Interfaces/IAccountMappingService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IDocumentService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IJournalEntryReportingService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IVatReportCsvExporter.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IYearEndClosingService.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/VatQuarterHelper.cs`
- Delete: `src/KoalaBooks.Application/Services/IAccountMappingService.cs`, `IDocumentService.cs`, `IJournalEntryReportingService.cs`, `IVatReportCsvExporter.cs`, `IYearEndClosingService.cs`, `VatQuarterHelper.cs`
- Modify: `src/KoalaBooks.Application/Services/AccountMappingService.cs` (remove DTOs, add using)
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs` (remove DTOs only — using already present)
- Modify: `src/KoalaBooks.Application/Services/JournalEntryService.cs` (remove DTOs only — using already added in Task 2)
- Modify: `src/KoalaBooks.Application/Services/VatReportCsvExporter.cs` (add using)
- Modify: `src/KoalaBooks.Application/Services/YearEndClosingService.cs` (remove DTOs, add using)

**Interfaces:**
- Produces: `KoalaBooks.Domain.Interfaces.IAccountMappingService` + `MappingRow`, `ApplyMappingResult`; `.IDocumentService` + `DocumentMeta`, `ZipImportResult`; `.IJournalEntryReportingService` + `TrialBalanceRow`, `GeneralLedgerAccountSection`, `GeneralLedgerRow`, `DashboardStats`, `BalanceSheetSection`, `BalanceSheetRow`, `IncomeStatementSection`, `IncomeStatementRow`, `VatReportData`, `VatReportSection`, `VatReportRow`; `.IVatReportCsvExporter`; `.IYearEndClosingService` + `ClosingValidationResult`, `ClosingPreview`, `ClosingEntryPreview`, `ClosingLinePreview`, `ClosingResult`; `.VatQuarterHelper` (static class).

- [ ] **Step 1: Create `IAccountMappingService.cs`**

`src/KoalaBooks.Domain/Interfaces/IAccountMappingService.cs`:
```csharp
namespace KoalaBooks.Domain.Interfaces;

public record MappingRow(
    string SourceAccountNumber,
    string SourceAccountName,
    decimal Ub,
    string? TargetAccountNumber);

public record ApplyMappingResult(int Mapped, int Skipped);

public interface IAccountMappingService
{
    Task<List<MappingRow>> BuildMappingAsync(int sourceFiscalYearId, int targetFiscalYearId);
    Task<ApplyMappingResult> ApplyMappingAsync(
        int sourceFiscalYearId,
        int targetFiscalYearId,
        List<MappingRow> rows);
}
```

Remove the interface and DTOs from `src/KoalaBooks.Application/Services/AccountMappingService.cs`. It currently starts:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public record MappingRow(
    string SourceAccountNumber,
    string SourceAccountName,
    decimal Ub,
    string? TargetAccountNumber);

public record ApplyMappingResult(int Mapped, int Skipped);

public class AccountMappingService : IAccountMappingService
```
Change it to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class AccountMappingService : IAccountMappingService
```

- [ ] **Step 2: Create `IDocumentService.cs`**

`src/KoalaBooks.Domain/Interfaces/IDocumentService.cs`:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Interfaces;

public class DocumentMeta
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? ClassifiedType { get; set; }
    public string? SuggestedType { get; set; }
    public string? ExtractedDataJson { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public ExtractionStatus ExtractionStatus { get; set; }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:N1} KB",
        _ => $"{FileSize / (1024.0 * 1024):N1} MB"
    };

    /// <summary>
    /// Resolves the date to pre-fill in a document's date field: the persisted
    /// (possibly user-edited) document date takes precedence over the AI-extracted
    /// invoice date, since it reflects the value the user last confirmed.
    /// </summary>
    public static DateTime? ResolvePrefillDate(DateOnly? documentDate, DateOnly? extractedInvoiceDate) =>
        (documentDate ?? extractedInvoiceDate)?.ToDateTime(TimeOnly.MinValue);
}

public record ZipImportResult(IReadOnlyList<Document> Imported, IReadOnlyList<(string FileName, string Reason)> Skipped);

public interface IDocumentService
{
    Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Func<Stream> openData);
    Task<string?> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate);
    Task<List<DocumentMeta>> GetPendingAsync(
        string? typeFilter = null,
        int skip = 0,
        int? take = null,
        string sortBy = "uploadedAt",
        bool sortAsc = false);
    Task<int> GetPendingCountAsync(string? typeFilter = null);
    Task<List<DocumentMeta>> GetLinkedAsync(DocumentEntityType entityType, int entityId);
    Task<Dictionary<int, int>> GetCountsForJournalEntriesAsync(IEnumerable<int> entryIds);
    Task<(string ContentType, byte[] Data, string FileName)?> GetDownloadAsync(int documentId);
    Task<bool> DeleteAsync(int documentId);
    Task LinkAsync(int documentId, DocumentEntityType entityType, int entityId);
    Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId);
    Task<(ZipImportResult? Result, string? Error)> UploadZipAsync(byte[] zipData);
}
```

Remove `DocumentMeta` and `ZipImportResult` (currently lines 425–454) from the end of `src/KoalaBooks.Application/Services/DocumentService.cs`. It currently ends:
```csharp
        return rows.Select(r => r.Meta).ToList();
    }
}

public class DocumentMeta
{
    ...
}

public record ZipImportResult(IReadOnlyList<Document> Imported, IReadOnlyList<(string FileName, string Reason)> Skipped);
```
Change it to end at the class's closing brace:
```csharp
        return rows.Select(r => r.Meta).ToList();
    }
}
```
No `using` change needed in `DocumentService.cs` — it already has `using KoalaBooks.Domain.Interfaces;`.

- [ ] **Step 3: Create `IJournalEntryReportingService.cs`**

`src/KoalaBooks.Domain/Interfaces/IJournalEntryReportingService.cs`:
```csharp
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Interfaces;

public class TrialBalanceRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public AccountClass AccountClass { get; set; }
    public decimal IncomingBalance { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal Balance => AccountClass.IsCreditNormal()
        ? IncomingBalance + TotalCredit - TotalDebit
        : IncomingBalance + TotalDebit - TotalCredit;
}

public class GeneralLedgerAccountSection
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal IncomingBalance { get; set; }
    public List<GeneralLedgerRow> Rows { get; set; } = [];
    public decimal ClosingBalance { get; set; }
}

public class GeneralLedgerRow
{
    public DateOnly Date { get; set; }
    public int EntryNumber { get; set; }
    public string Description { get; set; } = "";
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal RunningBalance { get; set; }
}

public class DashboardStats
{
    public int EntryCount { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
}

public class BalanceSheetSection
{
    public string Title { get; set; } = "";
    public List<BalanceSheetRow> Rows { get; set; } = [];
    public decimal Total { get; set; }
}

public class BalanceSheetRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal IncomingBalance { get; set; }
    public decimal PeriodDebit { get; set; }
    public decimal PeriodCredit { get; set; }
    public decimal ClosingBalance { get; set; }
}

public class IncomeStatementSection
{
    public string Title { get; set; } = "";
    public List<IncomeStatementRow> Rows { get; set; } = [];
    public decimal Total { get; set; }
}

public class IncomeStatementRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal Amount { get; set; }
}

public class VatReportData
{
    public VatReportSection OutputVat { get; set; } = new();
    public VatReportSection InputVat { get; set; } = new();
    public decimal NetPayable { get; set; }
}

public class VatReportSection
{
    public string Title { get; set; } = "";
    public List<VatReportRow> Rows { get; set; } = [];
    public decimal Total { get; set; }
}

public class VatReportRow
{
    public string AccountNumber { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

public interface IJournalEntryReportingService
{
    Task<List<TrialBalanceRow>> GetTrialBalanceAsync(int fiscalYearId, bool excludeClosingEntries = true);
    Task<GeneralLedgerAccountSection?> GetAccountLedgerAsync(
        int fiscalYearId, int accountId, DateOnly? from = null, DateOnly? to = null,
        bool excludeClosingEntries = true);
    Task<List<GeneralLedgerAccountSection>> GetGeneralLedgerAsync(
        int fiscalYearId, string? fromAccount = null, string? toAccount = null,
        DateOnly? from = null, DateOnly? to = null, bool excludeClosingEntries = true,
        bool hideEmpty = false);
    Task<Dictionary<int, (decimal IB, decimal UB)>> GetComputedBalancesAsync(int fiscalYearId);
    Task<HashSet<int>> GetAccountIdsWithTransactionsAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        bool includeClosingEntries = false);
    Task<List<BalanceSheetSection>> GetBalanceSheetAsync(int fiscalYearId, bool excludeClosingEntries = false);
    Task<(List<IncomeStatementSection> Sections, decimal NetResult)> GetIncomeStatementAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null, bool excludeClosingEntries = true);
    Task<VatReportData> GetVatReportAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null);
    Task<DashboardStats> GetDashboardStatsAsync(int fiscalYearId);
}
```

Remove the 11 DTO classes (currently lines 838–928, everything after `JournalEntryService`'s closing brace) from the end of `src/KoalaBooks.Application/Services/JournalEntryService.cs`. It currently ends:
```csharp
    }
}

public class TrialBalanceRow
{
    ...
}

... (10 more DTO classes) ...

public class VatReportRow
{
    ...
}
```
Delete everything from `public class TrialBalanceRow` to the end of file, leaving the file ending at the `JournalEntryService` class's closing brace. No `using` change needed here — Task 2 already added `using KoalaBooks.Domain.Interfaces;` to this file for `IJournalEntryService`.

- [ ] **Step 4: Create `IVatReportCsvExporter.cs`**

`src/KoalaBooks.Domain/Interfaces/IVatReportCsvExporter.cs`:
```csharp
namespace KoalaBooks.Domain.Interfaces;

public interface IVatReportCsvExporter
{
    byte[] Build(VatReportData data, string fiscalYearName, DateOnly? from, DateOnly? to);
}
```

In `src/KoalaBooks.Application/Services/VatReportCsvExporter.cs`, change:
```csharp
using System.Globalization;
using System.IO;
using System.Text;

namespace KoalaBooks.Application.Services;
```
to:
```csharp
using System.Globalization;
using System.IO;
using System.Text;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Services;
```

- [ ] **Step 5: Create `IYearEndClosingService.cs`**

`src/KoalaBooks.Domain/Interfaces/IYearEndClosingService.cs`:
```csharp
namespace KoalaBooks.Domain.Interfaces;

public record ClosingValidationResult(bool IsValid, List<string> Errors);

public record ClosingPreview(
    bool IsValid,
    List<string> Errors,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetResult,
    List<ClosingEntryPreview> Entries);

public record ClosingEntryPreview(string Description, List<ClosingLinePreview> Lines);

public record ClosingLinePreview(string AccountNumber, string AccountName, decimal Debit, decimal Credit);

public record ClosingResult(bool Success, string? Error, int? ClosingEntry1Number, int? ClosingEntry2Number);

public interface IYearEndClosingService
{
    Task<ClosingValidationResult> ValidateForClosingAsync(int fiscalYearId);
    Task<ClosingPreview> PreviewClosingAsync(int fiscalYearId);
    Task<ClosingResult> ExecuteClosingAsync(int fiscalYearId);
}
```

In `src/KoalaBooks.Application/Services/YearEndClosingService.cs`, remove the 5 DTO records and add the using. It currently starts:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public record ClosingValidationResult(bool IsValid, List<string> Errors);

public record ClosingPreview(
    bool IsValid,
    List<string> Errors,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetResult,
    List<ClosingEntryPreview> Entries);

public record ClosingEntryPreview(string Description, List<ClosingLinePreview> Lines);

public record ClosingLinePreview(string AccountNumber, string AccountName, decimal Debit, decimal Credit);

public record ClosingResult(bool Success, string? Error, int? ClosingEntry1Number, int? ClosingEntry2Number);

public class YearEndClosingService : IYearEndClosingService
```
Change it to:
```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class YearEndClosingService : IYearEndClosingService
```

- [ ] **Step 6: Create `VatQuarterHelper.cs`**

`src/KoalaBooks.Domain/Interfaces/VatQuarterHelper.cs` (moves verbatim, namespace only changes):
```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public static class VatQuarterHelper
{
    /// <summary>
    /// Returns the [From, To] date range for a Skatteverket calendar quarter (K1–K4)
    /// within the given fiscal year, clamped to fiscal year bounds.
    ///
    /// For broken fiscal years spanning two calendar years (e.g. Jul 2025–Jun 2026),
    /// the quarter is looked up in whichever year it actually falls inside the FY.
    /// Returns null if the quarter is entirely outside the fiscal year.
    /// </summary>
    public static (DateOnly From, DateOnly To)? ComputeRange(FiscalYear fy, int quarter)
    {
        if (quarter is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(quarter), "Quarter must be 1–4.");

        var (qFrom, qTo) = CalendarQuarter(fy.StartDate.Year, quarter);

        // For broken fiscal years, the quarter may belong to the end year instead.
        if (fy.StartDate.Year != fy.EndDate.Year && !Overlaps(qFrom, qTo, fy.StartDate, fy.EndDate))
            (qFrom, qTo) = CalendarQuarter(fy.EndDate.Year, quarter);

        var from = qFrom < fy.StartDate ? fy.StartDate : qFrom;
        var to   = qTo   > fy.EndDate   ? fy.EndDate   : qTo;
        return to < from ? null : (from, to);
    }

    private static (DateOnly From, DateOnly To) CalendarQuarter(int year, int quarter)
    {
        var from = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        return (from, from.AddMonths(3).AddDays(-1));
    }

    private static bool Overlaps(DateOnly qFrom, DateOnly qTo, DateOnly fyStart, DateOnly fyEnd)
        => qFrom <= fyEnd && qTo >= fyStart;
}
```

- [ ] **Step 7: Delete the 6 old files from Application.Services**

```bash
git rm src/KoalaBooks.Application/Services/IAccountMappingService.cs \
       src/KoalaBooks.Application/Services/IDocumentService.cs \
       src/KoalaBooks.Application/Services/IJournalEntryReportingService.cs \
       src/KoalaBooks.Application/Services/IVatReportCsvExporter.cs \
       src/KoalaBooks.Application/Services/IYearEndClosingService.cs \
       src/KoalaBooks.Application/Services/VatQuarterHelper.cs
```

- [ ] **Step 8: Verify Domain and Application build cleanly**

```bash
dotnet build src/KoalaBooks.Domain/KoalaBooks.Domain.csproj
dotnet build src/KoalaBooks.Application/KoalaBooks.Application.csproj
```

Expected: both succeed. At this point every one of the 14 interfaces + `VatQuarterHelper` is out of `Application.Services`; `Web`/`Components`/`Client`/`Tests` are still expected to fail until Tasks 5, 6, 7.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/IAccountMappingService.cs \
        src/KoalaBooks.Domain/Interfaces/IDocumentService.cs \
        src/KoalaBooks.Domain/Interfaces/IJournalEntryReportingService.cs \
        src/KoalaBooks.Domain/Interfaces/IVatReportCsvExporter.cs \
        src/KoalaBooks.Domain/Interfaces/IYearEndClosingService.cs \
        src/KoalaBooks.Domain/Interfaces/VatQuarterHelper.cs \
        src/KoalaBooks.Application/Services/AccountMappingService.cs \
        src/KoalaBooks.Application/Services/DocumentService.cs \
        src/KoalaBooks.Application/Services/JournalEntryService.cs \
        src/KoalaBooks.Application/Services/VatReportCsvExporter.cs \
        src/KoalaBooks.Application/Services/YearEndClosingService.cs
git commit -m "Move remaining service interfaces + VatQuarterHelper to Domain.Interfaces"
```

---

## Task 5: Update Components razor files and swap the ProjectReference

All 14 interfaces (+ helper) are now in `Domain.Interfaces`. Update every `.razor` file's `@using KoalaBooks.Application.Services` to `@using KoalaBooks.Domain.Interfaces`, fix `VatReport.razor`'s `@using static` line, then swap `Components.csproj`'s `ProjectReference` from `Application` to `Domain`. This is the change that structurally removes `Application → Infrastructure` from the WASM build graph.

**Files:**
- Modify (25 files, one-line `@using` swap each): `src/KoalaBooks.Components/Layout/MainLayout.razor`, `src/KoalaBooks.Components/Pages/AccountMapping.razor`, `Accounts.razor`, `BalanceSheet.razor`, `BankImport.razor`, `CustomerInvoices.razor`, `Customers.razor`, `FiscalYears.razor`, `GeneralLedger.razor`, `Home.razor`, `Inbox.razor`, `IncomeStatement.razor`, `Journal.razor`, `Review.razor`, `Settings.razor`, `SieExport.razor`, `SieImport.razor`, `SupplierInvoices.razor`, `Todo.razor`, `TrialBalance.razor`, `VatReport.razor`, `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor`, `JournalReviewSection.razor`, `PreviewDocumentDialog.razor`, `ReversalPreviewDialog.razor`
- Modify: `src/KoalaBooks.Components/KoalaBooks.Components.csproj`

**Interfaces:**
- Consumes: all of `KoalaBooks.Domain.Interfaces` (Tasks 2 and 4).

- [ ] **Step 1: Fix `Home.razor` first — it has CRLF line endings**

`src/KoalaBooks.Components/Pages/Home.razor` is checked into git with CRLF line endings (confirm with `git ls-files --eol src/KoalaBooks.Components/Pages/Home.razor` — it shows `i/crlf w/crlf`, unlike every other file in this task). Its `@using KoalaBooks.Application.Services` line therefore actually ends in `\r\n`. The `$`-anchored `grep`/`sed` patterns used in Step 2 below require an exact end-of-line match and silently fail to match a line ending in `\r` — no error, no duplicate-warning, the file is just skipped. Since `Home.razor`'s `@code` block directly uses `IFiscalYearService`, `IJournalEntryReportingService`, and `DashboardStats` (all moving to `Domain.Interfaces`), leaving it unfixed breaks the Components build in Step 6 with CS0246 once the `ProjectReference` swap lands in Step 4. Fix it on its own, first, with an unanchored substitution (safe here — the file has exactly one occurrence of this string, so an anchored match isn't needed for precision):

```bash
sed -i 's/@using KoalaBooks.Application.Services/@using KoalaBooks.Domain.Interfaces/' src/KoalaBooks.Components/Pages/Home.razor
```

This preserves the file's existing CRLF ending on that line since the substitution only replaces the matched substring, not the trailing `\r`.

- [ ] **Step 2: Bulk-swap the `@using` line across the remaining 24 files**

9 of the remaining 24 files (`MainLayout.razor`, `Accounts.razor`, `BankImport.razor`, `FiscalYears.razor`, `SieExport.razor`, `SieImport.razor`, `Todo.razor`, `ClassifyDocumentDialog.razor`, `PreviewDocumentDialog.razor`) **already** have a separate `@using KoalaBooks.Domain.Interfaces` line (added for other, already-in-Domain interfaces like `IBankImportService`/`ISieExportService`). For those, a blind replace would produce a duplicate `@using KoalaBooks.Domain.Interfaces` line in the same file — so delete the now-redundant `@using KoalaBooks.Application.Services` line instead of replacing it. For the other 15 files, replace in place. `Home.razor` is already fixed by Step 1 and, being CRLF, won't match this `$`-anchored `grep` anyway — no need to exclude it explicitly:

```bash
cd src/KoalaBooks.Components
grep -rl '^@using KoalaBooks.Application.Services$' . | while read -r f; do
  if grep -q '^@using KoalaBooks.Domain.Interfaces$' "$f"; then
    sed -i '/^@using KoalaBooks.Application.Services$/d' "$f"
  else
    sed -i 's/^@using KoalaBooks.Application.Services$/@using KoalaBooks.Domain.Interfaces/' "$f"
  fi
done
cd ../..
```

- [ ] **Step 2b: Confirm no duplicate `@using KoalaBooks.Domain.Interfaces` lines were introduced, and that no file still references `Application.Services`**

```bash
grep -rc '^@using KoalaBooks.Domain.Interfaces$' src/KoalaBooks.Components/ | awk -F: '$2 > 1 {print}'
grep -rn "KoalaBooks.Application.Services" src/KoalaBooks.Components/ --include=*.razor
```

Expected: no output from either command. A duplicate `using` would compile fine but risks tripping `TreatWarningsAsErrors` (CS0105) under the Release CI build (`.github/workflows/ci.yml` passes `/p:TreatWarningsAsErrors=true`) — if the first command prints anything, open that file and remove the duplicate line. The second command is a CRLF-agnostic sweep (unanchored, so it catches anything Step 1/2's anchored patterns could miss for the same reason `Home.razor` needed a separate fix) — if it prints anything, that file was missed and needs the same treatment as `Home.razor`.

- [ ] **Step 2: Verify the swap didn't touch `ReversalPreviewDialog.razor`'s static using of the concrete class**

```bash
grep -n "@using" src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor
```

Expected output includes both:
```
@using KoalaBooks.Domain.Interfaces
@using static KoalaBooks.Application.Services.JournalEntryService
```
The second line must be unchanged — `JournalEntryService` is the concrete class, which stays in `Application.Services`. The `sed` anchored the match to the exact full line `@using KoalaBooks.Application.Services`, so this static-using line (which has extra text after it) is untouched. If it was accidentally changed, revert that one line manually.

- [ ] **Step 3: Fix VatReport.razor's `@using static` line for `VatQuarterHelper`**

`src/KoalaBooks.Components/Pages/VatReport.razor` currently starts:
```razor
@page "/reports/vat"
@using System.IO
@using KoalaBooks.Domain.Interfaces
@using KoalaBooks.Domain.Entities
@using static KoalaBooks.Application.Services.VatQuarterHelper
@inject IJSRuntime JS
@inject IVatReportCsvExporter CsvExporter
```
Change the `@using static` line to:
```razor
@page "/reports/vat"
@using System.IO
@using KoalaBooks.Domain.Interfaces
@using KoalaBooks.Domain.Entities
@using static KoalaBooks.Domain.Interfaces.VatQuarterHelper
@inject IJSRuntime JS
@inject IVatReportCsvExporter CsvExporter
```

- [ ] **Step 4: Swap Components.csproj's ProjectReference**

`src/KoalaBooks.Components/KoalaBooks.Components.csproj` currently has:
```xml
  <ItemGroup>
    <ProjectReference Include="..\KoalaBooks.Application\KoalaBooks.Application.csproj" />
  </ItemGroup>
```
Change it to:
```xml
  <ItemGroup>
    <ProjectReference Include="..\KoalaBooks.Domain\KoalaBooks.Domain.csproj" />
  </ItemGroup>
```

- [ ] **Step 5: Confirm no more references to KoalaBooks.Application remain in Components**

```bash
grep -rn "KoalaBooks.Application" src/KoalaBooks.Components/ --include=*.razor --include=*.csproj
```

Expected: no output.

- [ ] **Step 6: Verify Components builds cleanly**

```bash
dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj
```

Expected: succeeds. (`Client` is still expected to fail until Task 6; `Web` was already fixed in Task 3 and should now also build since it depends on `Components`.)

```bash
dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj
```

Expected: succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Components/
git commit -m "Swap Components ProjectReference from Application to Domain"
```

---

## Task 6: Update Client and add the two missing WASM API services

`Client/Program.cs` and its three existing `*ApiService.cs` files still say `using KoalaBooks.Application.Services;`, which compiled only via the now-removed `Components → Application` transitivity. Fix those, then add `BankImportApiService` and `SupplierInvoiceApiService` — `MainLayout` resolves `IBankImportService`/`ISupplierInvoiceService` for nav badge counts via `ScopeFactory.CreateAsyncScope()`, and today only `IFiscalYearService`, `IAccountService`, `IJournalEntryService` have WASM registrations. Without this, `MainLayout` throws `InvalidOperationException` from a missing DI registration the moment WASM ever wins the `InteractiveAuto` render race for `/review` in a real deployment (confirmed it doesn't win locally today, per prior testing — see `project_wasm_auto_rendermode_never_wins_dev` memory).

**Files:**
- Modify: `src/KoalaBooks.Client/Program.cs`
- Modify: `src/KoalaBooks.Client/Services/FiscalYearApiService.cs`
- Modify: `src/KoalaBooks.Client/Services/AccountApiService.cs`
- Modify: `src/KoalaBooks.Client/Services/JournalEntryApiService.cs`
- Create: `src/KoalaBooks.Client/Services/BankImportApiService.cs`
- Create: `src/KoalaBooks.Client/Services/SupplierInvoiceApiService.cs`

**Interfaces:**
- Consumes: `KoalaBooks.Domain.Interfaces.IBankImportService` (existing, already in Domain), `.ISupplierInvoiceService` (Task 2), `ApiJson.Options` from `src/KoalaBooks.Client/Services/ApiJson.cs` (existing).
- Produces: `KoalaBooks.Client.Services.BankImportApiService : IBankImportService`, `SupplierInvoiceApiService : ISupplierInvoiceService`, registered in `Client/Program.cs`.

- [ ] **Step 1: Fix the using in the 3 existing files**

`src/KoalaBooks.Client/Program.cs` — change line 1 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

`src/KoalaBooks.Client/Services/FiscalYearApiService.cs` — change line 2 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

`src/KoalaBooks.Client/Services/AccountApiService.cs` — change line 2 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

`src/KoalaBooks.Client/Services/JournalEntryApiService.cs` — change line 2 `using KoalaBooks.Application.Services;` to `using KoalaBooks.Domain.Interfaces;`.

- [ ] **Step 2: Create BankImportApiService.cs**

`src/KoalaBooks.Client/Services/BankImportApiService.cs`:
```csharp
using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

// HTTP-backed IBankImportService for the WASM render tree — MainLayout only needs the
// unmatched-count nav badge; everything else has no REST endpoint and isn't needed by
// the WASM-rendered /review page.
public class BankImportApiService(HttpClient http) : IBankImportService
{
    public async Task<int> CountUnmatchedAsync(int fiscalYearId)
    {
        var response = await http.GetFromJsonAsync<CountResponse>(
            $"api/v1/fiscal-years/{fiscalYearId}/bank-transactions/unmatched-count", ApiJson.Options).ConfigureAwait(false);
        return response?.Count ?? 0;
    }

    // Everything below has no REST endpoint yet and isn't needed by the WASM-rendered /review
    // page. Task-returning members use Task.FromException so the failure surfaces on await like
    // a real async call, instead of throwing synchronously at the call site.
    public BankFileParseResult ParseFile(Stream stream, string fileName) =>
        throw new NotSupportedException("Bank file parsing has no REST endpoint yet.");

    public Task<List<BankTransactionPreview>> BuildPreviewAsync(
        int accountId, List<string[]> rows, int dateCol, int amountCol, int descCol, int? refCol, string dateFormat) =>
        Task.FromException<List<BankTransactionPreview>>(
            new NotSupportedException("Bank import preview has no REST endpoint yet."));

    public Task<BankImportResult> ImportAsync(int accountId, List<BankTransactionPreview> previews) =>
        Task.FromException<BankImportResult>(
            new NotSupportedException("Bank import has no REST endpoint yet."));

    public Task<List<BankTransaction>> GetUnmatchedAsync(int fiscalYearId) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Fetching unmatched bank transactions has no REST endpoint yet."));

    public Task<List<BankTransaction>> GetByAccountAsync(int accountId) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Fetching bank transactions by account has no REST endpoint yet."));

    public Task<List<BankTransaction>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from, DateOnly? to, int? accountId) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Fetching bank transactions by fiscal year has no REST endpoint yet."));

    public Task<BankTransaction?> GetByIdAsync(int id) =>
        Task.FromException<BankTransaction?>(
            new NotSupportedException("Fetching a bank transaction by id has no REST endpoint yet."));

    public Task<List<Account>> GetImportableAccountsAsync(int fiscalYearId, string prefix) =>
        Task.FromException<List<Account>>(
            new NotSupportedException("Fetching importable accounts has no REST endpoint yet."));

    public Task SetStatusAsync(int bankTransactionId, BankTransactionStatus status) =>
        Task.FromException(
            new NotSupportedException("Setting bank transaction status has no REST endpoint yet."));

    public Task<string?> MatchToEntryAsync(int bankTransactionId, int journalEntryId) =>
        Task.FromException<string?>(
            new NotSupportedException("Matching a bank transaction to an entry has no REST endpoint yet."));

    public Task<List<JournalEntry>> GetUnmatchedJournalEntriesAsync(int fiscalYearId, int bankAccountId) =>
        Task.FromException<List<JournalEntry>>(
            new NotSupportedException("Fetching unmatched journal entries has no REST endpoint yet."));

    public Task<int?> SuggestContraAccountAsync(int bankAccountId, string description, decimal amount) =>
        Task.FromException<int?>(
            new NotSupportedException("Contra account suggestion has no REST endpoint yet."));

    private record CountResponse(int Count);
}
```

- [ ] **Step 3: Create SupplierInvoiceApiService.cs**

`src/KoalaBooks.Client/Services/SupplierInvoiceApiService.cs`:
```csharp
using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

// HTTP-backed ISupplierInvoiceService for the WASM render tree — MainLayout only needs
// the unpaid-count nav badge; everything else has no REST endpoint and isn't needed by
// the WASM-rendered /review page.
public class SupplierInvoiceApiService(HttpClient http) : ISupplierInvoiceService
{
    public async Task<int> CountUnpaidAsync(int fiscalYearId)
    {
        var response = await http.GetFromJsonAsync<CountResponse>(
            $"api/v1/fiscal-years/{fiscalYearId}/supplier-invoices/unpaid-count", ApiJson.Options).ConfigureAwait(false);
        return response?.Count ?? 0;
    }

    // Everything below has no REST endpoint yet and isn't needed by the WASM-rendered /review
    // page. Task.FromException surfaces the failure on await, like a real async call, instead of
    // throwing synchronously at the call site.
    public Task<List<SupplierInvoice>> GetAllAsync(int fiscalYearId) =>
        Task.FromException<List<SupplierInvoice>>(
            new NotSupportedException("Fetching supplier invoices has no REST endpoint yet."));

    public Task<SupplierInvoice?> GetByIdAsync(int id) =>
        Task.FromException<SupplierInvoice?>(
            new NotSupportedException("Fetching a supplier invoice by id has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> UpdateAsync(SupplierInvoice invoice) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Updating a supplier invoice has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> CreateAsync(SupplierInvoice invoice) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Creating a supplier invoice has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int expenseAccountId, int payableAccountId, int? vatAccountId) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Posting a supplier invoice has no REST endpoint yet."));

    public Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Finding matching bank transactions has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int payableAccountId,
        int? linkBankTransactionId = null) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Marking a supplier invoice as paid has no REST endpoint yet."));

    public Task<string?> DeleteAsync(int invoiceId) =>
        Task.FromException<string?>(
            new NotSupportedException("Deleting a supplier invoice has no REST endpoint yet."));

    public Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix) =>
        Task.FromException<Account?>(
            new NotSupportedException("Finding an account by prefix has no REST endpoint yet."));

    public Task<List<string>> GetSuppliersAsync(int fiscalYearId) =>
        Task.FromException<List<string>>(
            new NotSupportedException("Fetching supplier names has no REST endpoint yet."));

    public Task<HashSet<int>> GetLinkedJournalEntryIdsAsync(int fiscalYearId) =>
        Task.FromException<HashSet<int>>(
            new NotSupportedException("Fetching linked journal entry ids has no REST endpoint yet."));

    public Task<List<JournalEntry>> GetLinkableEntriesAsync(int fiscalYearId) =>
        Task.FromException<List<JournalEntry>>(
            new NotSupportedException("Fetching linkable journal entries has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> CreateFromEntryAsync(
        int journalEntryId,
        SupplierInvoice invoice) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Creating a supplier invoice from an entry has no REST endpoint yet."));

    private record CountResponse(int Count);
}
```

- [ ] **Step 4: Register both new services in Client/Program.cs**

`src/KoalaBooks.Client/Program.cs` currently ends:
```csharp
// PoC scope: only the services the WASM-rendered /review page needs.
builder.Services.AddScoped<IFiscalYearService, FiscalYearApiService>();
builder.Services.AddScoped<IAccountService, AccountApiService>();
builder.Services.AddScoped<IJournalEntryService, JournalEntryApiService>();

await builder.Build().RunAsync();
```
Change it to:
```csharp
// PoC scope: only the services the WASM-rendered /review page needs.
builder.Services.AddScoped<IFiscalYearService, FiscalYearApiService>();
builder.Services.AddScoped<IAccountService, AccountApiService>();
builder.Services.AddScoped<IJournalEntryService, JournalEntryApiService>();
// MainLayout's nav badge counts resolve these via ScopeFactory regardless of which page
// is rendering, so they're needed even though no WASM page injects them directly.
builder.Services.AddScoped<IBankImportService, BankImportApiService>();
builder.Services.AddScoped<ISupplierInvoiceService, SupplierInvoiceApiService>();

await builder.Build().RunAsync();
```

- [ ] **Step 5: Verify Client builds cleanly**

```bash
dotnet build src/KoalaBooks.Client/KoalaBooks.Client.csproj
```

Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Client/
git commit -m "Fix Client usings and add BankImportApiService/SupplierInvoiceApiService for MainLayout"
```

---

## Task 7: Update Tests and verify the whole solution builds and passes

Of the 14 test files that `using KoalaBooks.Application.Services;`, 10 already also import `KoalaBooks.Domain.Interfaces` (for other reasons) and need no change — the moved types just start resolving through that existing using. Only 4 reference a moved type by name directly and need the using added: `AccountMappingServiceTests.cs` (`MappingRow`), `VatReportTests.cs` (`VatReportData`/`VatReportSection`/`VatReportRow`), `DocumentMetaTests.cs` (`DocumentMeta`), `VatQuarterHelperTests.cs` (`VatQuarterHelper`).

**Files:**
- Modify: `tests/KoalaBooks.Tests/AccountMappingServiceTests.cs`
- Modify: `tests/KoalaBooks.Tests/VatReportTests.cs`
- Modify: `tests/KoalaBooks.Tests/DocumentMetaTests.cs`
- Modify: `tests/KoalaBooks.Tests/VatQuarterHelperTests.cs`

**Interfaces:**
- Consumes: `KoalaBooks.Domain.Interfaces.MappingRow`, `.VatReportData`, `.DocumentMeta`, `.VatQuarterHelper` (Task 4).

- [ ] **Step 1: Add the using to the 4 files that need it**

`tests/KoalaBooks.Tests/AccountMappingServiceTests.cs` — change:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
```
to:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
```

`tests/KoalaBooks.Tests/VatReportTests.cs` — change:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
```
to:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
```

`tests/KoalaBooks.Tests/DocumentMetaTests.cs` — change:
```csharp
using KoalaBooks.Application.Services;
```
to:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Interfaces;
```

`tests/KoalaBooks.Tests/VatQuarterHelperTests.cs` — change:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
```
to:
```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
```

- [ ] **Step 2: Full solution build**

```bash
dotnet build
```

Expected: succeeds with zero errors across `Domain`, `Application`, `Infrastructure`, `Web`, `Components`, `Client`, `Tests`, `AppHostSupport`.

- [ ] **Step 3: Full test run**

```bash
dotnet test
```

Expected: all tests pass, same pass/fail set as before this plan started (namespace-only renames, no behavior change).

- [ ] **Step 4: Commit**

```bash
git add tests/KoalaBooks.Tests/AccountMappingServiceTests.cs \
        tests/KoalaBooks.Tests/VatReportTests.cs \
        tests/KoalaBooks.Tests/DocumentMetaTests.cs \
        tests/KoalaBooks.Tests/VatQuarterHelperTests.cs
git commit -m "Fix Tests usings for interfaces moved to Domain.Interfaces"
```

---

## Task 8: Bundle-size verification (clean Debug + Release, manual smoke test)

The whole point of this refactor is that the WASM bundle no longer contains server-only EF/Npgsql-coupled assemblies. Verify that structurally, not just "the build succeeded" — a clean rebuild is required because Debug and Release build the `_framework` folder independently, and stale `bin`/`obj` can mask a regression.

**Files:** none (verification only).

- [ ] **Step 1: Clean rebuild in Debug and inspect the framework folder**

```bash
rm -rf src/KoalaBooks.Client/bin src/KoalaBooks.Client/obj \
       src/KoalaBooks.Components/bin src/KoalaBooks.Components/obj \
       src/KoalaBooks.Application/bin src/KoalaBooks.Application/obj \
       src/KoalaBooks.Domain/bin src/KoalaBooks.Domain/obj
dotnet build src/KoalaBooks.Client/KoalaBooks.Client.csproj
ls src/KoalaBooks.Client/bin/Debug/net10.0/wwwroot/_framework/ | \
  grep -iE "pdfpig|npgsql|entityframeworkcore|openiddict|identity\.entityframeworkcore|dataprotection\.entityframeworkcore|exceldatareader"
```

Expected: the `grep` finds nothing (empty output, exit code 1). If anything matches, the decoupling didn't fully take — check that Task 5's `ProjectReference` swap actually landed and that no `.razor` file still resolves a type only reachable via `Application`.

- [ ] **Step 2: Repeat for Release publish**

```bash
rm -rf src/KoalaBooks.Client/bin src/KoalaBooks.Client/obj \
       src/KoalaBooks.Components/bin src/KoalaBooks.Components/obj \
       src/KoalaBooks.Application/bin src/KoalaBooks.Application/obj \
       src/KoalaBooks.Domain/bin src/KoalaBooks.Domain/obj
dotnet publish src/KoalaBooks.Client/KoalaBooks.Client.csproj -c Release
ls src/KoalaBooks.Client/bin/Release/net10.0/publish/wwwroot/_framework/ | \
  grep -iE "pdfpig|npgsql|entityframeworkcore|openiddict|identity\.entityframeworkcore|dataprotection\.entityframeworkcore|exceldatareader"
```

Expected: empty output, exit code 1.

- [ ] **Step 3: Full solution build one more time (Release, matching CI)**

```bash
dotnet build --configuration Release /p:TreatWarningsAsErrors=true /p:WarningsNotAsErrors=NU1903
dotnet test --configuration Release --no-build
```

Expected: both succeed, matching what CI (`.github/workflows/ci.yml`) runs.

- [ ] **Step 4: Manual smoke test**

Run the app (see the project's `run` skill or `dotnet run --project src/KoalaBooks.Web`), log in, and load `/review`. Confirm:
- The drafts list loads.
- Nav badge counts (unmatched bank transactions, unpaid supplier invoices) render without error in the layout.
- No console errors related to missing DI registrations or failed API calls.

This exercises the Server-rendered path (today's actual behavior per the `InteractiveAuto`-never-wins-locally finding) and confirms nothing broke for the common case. It does not exercise the WASM-rendered path locally — that's a known limitation already documented (see `project_wasm_auto_rendermode_never_wins_dev` memory) and out of scope to fix here.

- [ ] **Step 5: Report results**

No commit for this task (verification only). Summarize in the PR description: confirmed absence of the 7 forbidden assembly groups in both Debug and Release `_framework` output, full solution build + test green, manual `/review` smoke test passed.
