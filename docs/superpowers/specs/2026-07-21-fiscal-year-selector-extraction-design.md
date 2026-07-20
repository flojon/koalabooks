# Fiscal-year selector extraction (#308)

## Problem

PR #303 added a fiscal-year `<select>` to Accounts/BankImport/CustomerInvoices/SupplierInvoices. PR #306 (merged) added the same pattern to TrialBalance/BalanceSheet/IncomeStatement/VatReport/GeneralLedger/Journal. That's 10 pages now duplicating:

- The seed-resolution block (check `FiscalYearSelectionContext.LastSelectedFiscalYearId`, fall back to `IFiscalYearService.GetDefaultFiscalYearAsync()`, then a page-specific final fallback)
- The `<select>` toolbar markup (label + `@foreach` over a fiscal-year list)
- A change handler that writes the new selection back to `FiscalYearSelectionContext` and reloads/resets page state

Issue #308 asks to extract this before the count grows further.

## Constraint check

`docs/superpowers/plans/2026-07-19-fiscal-year-resolution.md` states: "No global fiscal-year selector component — per-page selection only, optionally seeded from the scoped `FiscalYearSelectionContext`." That constraint is about not sharing live selection *state* across pages via one component instance. It does not block factoring the repeated *markup and seed logic* into a reusable, stateless component that each page instantiates independently, owning its own local selected-id field. Confirmed with the user before proceeding on this basis.

## Design

### 1. `FiscalYearSelector.razor` (new, `src/KoalaBooks.Components/Shared/`)

Stateless presentational component, same convention as `AccountSearchDropdown.razor` (parameters in, `EventCallback<T>` out, no injected services, no base class):

```csharp
[Parameter, EditorRequired] public List<FiscalYear> FiscalYears { get; set; } = [];
[Parameter] public int SelectedFiscalYearId { get; set; }
[Parameter] public EventCallback<int> SelectedFiscalYearIdChanged { get; set; }
[Parameter] public string Width { get; set; } = "200px";
```

Renders the label + `<select>` + `@foreach` options markup currently duplicated across all 10 pages. On change, invokes `SelectedFiscalYearIdChanged` with the new id. Does not touch `FiscalYearSelectionContext` — that stays the host page's responsibility, same as today.

### 2. `ResolveSeedAsync` extension method (new, `src/KoalaBooks.Domain/`, next to `FiscalYearSelectionContext.cs`)

```csharp
public static async Task<FiscalYear?> ResolveSeedAsync(
    this FiscalYearSelectionContext context,
    IFiscalYearService fiscalYearService,
    List<FiscalYear> candidates,
    FiscalYear? extraFallback = null)
{
    FiscalYear? seed = null;
    if (context.LastSelectedFiscalYearId is { } lastId)
        seed = candidates.FirstOrDefault(f => f.Id == lastId);
    seed ??= await fiscalYearService.GetDefaultFiscalYearAsync() ?? extraFallback ?? candidates.FirstOrDefault();
    return seed;
}
```

Each page calls this once in `OnInitializedAsync`. For 9 of the 10 pages this is a direct drop-in with no `extraFallback`. Journal passes its existing "latest open year" fallback (`_activeFiscalYear`) as `extraFallback`, preserving its current fallback chain (last-selected → default → latest open → latest overall).

### 3. Per-page changes

Each page keeps:
- Its own local selected-id field (`_fiscalYearId` / `SelectedFiscalYearId`, naming unchanged per page)
- Its own change handler (`OnFiscalYearChangedAsync` / IncomeStatement's non-reloading `OnFiscalYearChanged`) — still the place that calls `SelectionContext.Set(...)` plus any page-specific reload/reset (Accounts recomputes `_otherFiscalYears`; VatReport clears `_data`/date range/`_quarterWarning`; Journal resets its five side panels; GeneralLedger goes through `ApplyFilters()`/`_isReloading`)
- Its own fiscal-year list fetch (`GetOpenFiscalYearsAsync()` for the 4 open-years pages, `GetAllAsync()` for the 6 all-years pages). Accounts keeps its existing inline open-year filter since it separately needs `allYears` for its copy-accounts panel.

Markup changes from the inline `<select>` block to `<FiscalYearSelector FiscalYears="..." SelectedFiscalYearId="..." SelectedFiscalYearIdChanged="OnFiscalYearChangedAsync" />`.

### 4. Testing

- New `AccountsPageTests.cs`, `BankImportPageTests.cs`, `CustomerInvoicesPageTests.cs`, `SupplierInvoicesPageTests.cs` under `tests/KoalaBooks.ComponentTests/`, following the existing `TrialBalancePageTests.cs` pattern (seed-from-context / fallback-to-default / write-back-on-change). These 4 pages currently have zero component tests; this closes that gap.
- New unit test covering `ResolveSeedAsync` directly: no prior selection, stale/not-in-candidates id, and the `extraFallback` path.
- The 6 existing PR #306 component test files must keep passing — `cut.Find("select")` still resolves since `FiscalYearSelector` renders a real `<select>` element into the host's render tree.

## Non-goals

- Not revisiting the single-shared-field vs dual-field design of `FiscalYearSelectionContext` (settled in PR #306).
- Not changing `IFiscalYearService`.
- Not adding a base-component/code-behind pattern — this codebase has neither, and the extraction doesn't need one.

## Verification

`dotnet build` (0 warnings/errors) and full test suite green, plus a manual pass on 2-3 representative pages (one open-years page, one all-years page, Journal for its extra fallback) via `aspire start --isolated` + playwright-cli, client-side nav only per the existing goto-tears-down-circuit gotcha.
