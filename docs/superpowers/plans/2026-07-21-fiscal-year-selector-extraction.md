# Fiscal-Year Selector Extraction (#308) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the fiscal-year `<select>` markup and seed-resolution logic duplicated across 10 pages (Accounts, BankImport, CustomerInvoices, SupplierInvoices, TrialBalance, BalanceSheet, IncomeStatement, VatReport, GeneralLedger, Journal) into one shared component and one shared extension method, per issue #308 and the design doc at `docs/superpowers/specs/2026-07-21-fiscal-year-selector-extraction-design.md`.

**Architecture:** A new stateless `FiscalYearSelector.razor` component (`src/KoalaBooks.Components/Shared/`) renders the label + `<select>` + `@foreach` options markup, following the exact convention of the existing `AccountSearchDropdown.razor` (parameters in, `EventCallback<int>` out, no injected services). Each host page wires it with `@bind-SelectedFiscalYearId="..."` / `@bind-SelectedFiscalYearId:after="..."` — the same Razor two-way-binding sugar already used for `AccountSearchDropdown` in this codebase — so the host's existing change-handler method (which still owns writing to `FiscalYearSelectionContext` and any page-specific reload/reset) keeps its current signature unchanged. A new `ResolveSeedAsync` extension method on `FiscalYearSelectionContext` (`src/KoalaBooks.Domain/`) replaces the duplicated seed-resolution block (`LastSelectedFiscalYearId` → `GetDefaultFiscalYearAsync()` → optional `extraFallback` → `candidates.FirstOrDefault()`). Neither the component nor the extension method touches `FiscalYearSelectionContext.Set(...)` — that stays each host page's responsibility, unchanged from today.

**Tech Stack:** .NET / Blazor Server (MudBlazor components), EF Core + PostgreSQL, xUnit + a real Postgres-backed `TestFixture` for non-component tests, bUnit + NSubstitute for component tests.

## Global Constraints

- The component is stateless and presentational: `[Parameter, EditorRequired] List<FiscalYear> FiscalYears`, `[Parameter] int SelectedFiscalYearId`, `[Parameter] EventCallback<int> SelectedFiscalYearIdChanged`, `[Parameter] string Width = "200px"` — no injected services, no base class, same convention as `src/KoalaBooks.Components/Shared/AccountSearchDropdown.razor`.
- The component renders only the `<label>Räkenskapsår:</label>` + `<select>` pair — NOT a wrapping `<div class="toolbar">` — because several host pages' toolbar `<div>` carries other page-specific content (extra CSS classes, date pickers, buttons) alongside the selector. The host page keeps its own `<div class="toolbar">` wrapper.
- `ResolveSeedAsync` signature is exact: `Task<FiscalYear?> ResolveSeedAsync(this FiscalYearSelectionContext context, IFiscalYearService fiscalYearService, List<FiscalYear> candidates, FiscalYear? extraFallback = null)` in namespace `KoalaBooks.Domain`, file `src/KoalaBooks.Domain/FiscalYearSelectionContextExtensions.cs` (next to `FiscalYearSelectionContext.cs` in the same namespace/folder).
- Default `Width="200px"` matches TrialBalance/BalanceSheet/IncomeStatement/VatReport/GeneralLedger (no explicit `Width` needed on those 5). Accounts, BankImport, CustomerInvoices, SupplierInvoices, and Journal currently render at `width:220px` and MUST pass `Width="220px"` explicitly — omitting it silently shrinks their selector by 20px.
- **Known, intentional behavior change:** Accounts, BankImport, CustomerInvoices, and SupplierInvoices currently have no final fallback (`seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();` with nothing after) — if no default fiscal year is set, the page renders with nothing preselected. `ResolveSeedAsync`'s unconditional `?? candidates.FirstOrDefault()` tail (needed for the 6 report/journal pages, which already have it) means these 4 pages now also fall back to the first candidate. This is expected per the design doc, not a bug to work around. Task 9 adds a regression test proving the new fallback fires.
- The 6 existing PR #306 component test files (`TrialBalancePageTests.cs`, `BalanceSheetPageTests.cs`, `IncomeStatementPageTests.cs`, `VatReportPageTests.cs`, `GeneralLedgerPageTests.cs`, `JournalPageTests.cs`) must keep passing UNMODIFIED — `cut.Find("select")` still resolves the fiscal-year selector because `FiscalYearSelector` renders a real `<select>` element into the host's render tree, and it remains the first `<select>` in each page's markup.
- Not revisiting the single-shared-field vs dual-field design of `FiscalYearSelectionContext` (settled in PR #306). Not changing `IFiscalYearService`. Not adding a base-component/code-behind pattern.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/KoalaBooks.Components/Shared/FiscalYearSelector.razor` *(new)* | Shared label + `<select>` + options markup |
| `tests/KoalaBooks.ComponentTests/FiscalYearSelectorTests.cs` *(new)* | Component-level rendering/callback tests |
| `src/KoalaBooks.Domain/FiscalYearSelectionContextExtensions.cs` *(new)* | `ResolveSeedAsync` extension method |
| `tests/KoalaBooks.Tests/FiscalYearSelectionContextResolveSeedTests.cs` *(new)* | Unit tests for `ResolveSeedAsync` against a real `TestFixture`-backed `IFiscalYearService` |
| `src/KoalaBooks.Components/Pages/TrialBalance.razor` | Adopt component + extension (Width default) |
| `src/KoalaBooks.Components/Pages/BalanceSheet.razor` | Adopt component + extension (Width default) |
| `src/KoalaBooks.Components/Pages/IncomeStatement.razor` | Adopt component + extension (Width default) |
| `src/KoalaBooks.Components/Pages/VatReport.razor` | Adopt component + extension (Width default) |
| `src/KoalaBooks.Components/Pages/GeneralLedger.razor` | Adopt component + extension (Width default) |
| `src/KoalaBooks.Components/Pages/Journal.razor` | Adopt component + extension (`Width="220px"`, `extraFallback: _activeFiscalYear`) |
| `src/KoalaBooks.Components/Pages/Accounts.razor` | Adopt component + extension (`Width="220px"`) |
| `src/KoalaBooks.Components/Pages/BankImport.razor` | Adopt component + extension (`Width="220px"`) |
| `src/KoalaBooks.Components/Pages/CustomerInvoices.razor` | Adopt component + extension (`Width="220px"`) |
| `src/KoalaBooks.Components/Pages/SupplierInvoices.razor` | Adopt component + extension (`Width="220px"`) |
| `tests/KoalaBooks.ComponentTests/AccountsPageTests.cs` *(new)* | Closes zero-coverage gap; includes the no-default-fallback regression test |
| `tests/KoalaBooks.ComponentTests/BankImportPageTests.cs` *(new)* | Closes zero-coverage gap |
| `tests/KoalaBooks.ComponentTests/CustomerInvoicesPageTests.cs` *(new)* | Closes zero-coverage gap |
| `tests/KoalaBooks.ComponentTests/SupplierInvoicesPageTests.cs` *(new)* | Closes zero-coverage gap |

---

### Task 1: `FiscalYearSelector.razor` shared component

**Files:**
- Create: `src/KoalaBooks.Components/Shared/FiscalYearSelector.razor`
- Test: `tests/KoalaBooks.ComponentTests/FiscalYearSelectorTests.cs`

**Interfaces:**
- Produces: `[Parameter, EditorRequired] List<FiscalYear> FiscalYears`, `[Parameter] int SelectedFiscalYearId`, `[Parameter] EventCallback<int> SelectedFiscalYearIdChanged`, `[Parameter] string Width = "200px"`. Consumed by Tasks 3–12.

- [ ] **Step 1: Write the failing component tests**

Create `tests/KoalaBooks.ComponentTests/FiscalYearSelectorTests.cs`:

```csharp
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.ComponentTests;

public class FiscalYearSelectorTests : BunitContext
{
    private static readonly FiscalYear Fy2025 = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31) };
    private static readonly FiscalYear Fy2026 = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31) };

    [Fact]
    public void RendersOneOptionPerFiscalYear()
    {
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id));

        var options = cut.FindAll("option");

        Assert.Equal(2, options.Count);
    }

    [Fact]
    public void DefaultWidth_Is200px()
    {
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id));

        Assert.Contains("width:200px", cut.Find("select").GetAttribute("style"));
    }

    [Fact]
    public void ExplicitWidth_OverridesDefault()
    {
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id)
            .Add(c => c.Width, "220px"));

        Assert.Contains("width:220px", cut.Find("select").GetAttribute("style"));
    }

    [Fact]
    public void ChangingSelection_InvokesCallbackWithNewId()
    {
        int? received = null;
        var cut = Render<FiscalYearSelector>(p => p
            .Add(c => c.FiscalYears, [Fy2025, Fy2026])
            .Add(c => c.SelectedFiscalYearId, Fy2026.Id)
            .Add(c => c.SelectedFiscalYearIdChanged, (int id) => received = id));

        cut.Find("select").Change(Fy2025.Id.ToString());

        Assert.Equal(Fy2025.Id, received);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~FiscalYearSelectorTests"`
Expected: FAIL — `KoalaBooks.Components.Shared.FiscalYearSelector` doesn't exist (compile error).

- [ ] **Step 3: Create the component**

Create `src/KoalaBooks.Components/Shared/FiscalYearSelector.razor`:

```razor
@using KoalaBooks.Domain.Entities

<label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
<select @bind="SelectedFiscalYearId" @bind:after="OnChangedAsync" style="width:@Width;">
    @foreach (var fy in FiscalYears)
    {
        <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
    }
</select>

@code {
    [Parameter, EditorRequired]
    public List<FiscalYear> FiscalYears { get; set; } = [];

    [Parameter]
    public int SelectedFiscalYearId { get; set; }

    [Parameter]
    public EventCallback<int> SelectedFiscalYearIdChanged { get; set; }

    [Parameter]
    public string Width { get; set; } = "200px";

    private async Task OnChangedAsync()
    {
        await SelectedFiscalYearIdChanged.InvokeAsync(SelectedFiscalYearId);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~FiscalYearSelectorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Shared/FiscalYearSelector.razor tests/KoalaBooks.ComponentTests/FiscalYearSelectorTests.cs
git commit -m "Add shared FiscalYearSelector component (#308)"
```

---

### Task 2: `ResolveSeedAsync` extension method

**Files:**
- Create: `src/KoalaBooks.Domain/FiscalYearSelectionContextExtensions.cs`
- Test: `tests/KoalaBooks.Tests/FiscalYearSelectionContextResolveSeedTests.cs`

**Interfaces:**
- Consumes: `FiscalYearSelectionContext.LastSelectedFiscalYearId` (`src/KoalaBooks.Domain/FiscalYearSelectionContext.cs`), `IFiscalYearService.GetDefaultFiscalYearAsync()` (`src/KoalaBooks.Domain/Interfaces/IFiscalYearService.cs`).
- Produces: `Task<FiscalYear?> ResolveSeedAsync(this FiscalYearSelectionContext context, IFiscalYearService fiscalYearService, List<FiscalYear> candidates, FiscalYear? extraFallback = null)`. Consumed by Tasks 3–12.

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/FiscalYearSelectionContextResolveSeedTests.cs`:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class FiscalYearSelectionContextResolveSeedTests : IDisposable
{
    private readonly TestFixture _f;

    public FiscalYearSelectionContextResolveSeedTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task LastSelectedIdInCandidates_WinsOverDefault()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var candidateYear = _f.CreateFiscalYear("Candidate", today.AddYears(-2), today.AddYears(-2).AddMonths(11));
        var defaultYear = _f.CreateFiscalYear("Default", today.AddMonths(-1), today.AddMonths(1));
        var candidates = new List<FiscalYear> { candidateYear, defaultYear };
        var ctx = new FiscalYearSelectionContext();
        ctx.Set(candidateYear.Id);

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates);

        Assert.NotNull(seed);
        Assert.Equal(candidateYear.Id, seed.Id);
    }

    [Fact]
    public async Task LastSelectedIdNotInCandidates_FallsBackToDefault()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var defaultYear = _f.CreateFiscalYear("Default", today.AddMonths(-1), today.AddMonths(1));
        var candidates = new List<FiscalYear> { defaultYear };
        var ctx = new FiscalYearSelectionContext();
        ctx.Set(999999); // stale id, not present in candidates

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates);

        Assert.NotNull(seed);
        Assert.Equal(defaultYear.Id, seed.Id);
    }

    [Fact]
    public async Task NoDefault_UsesExtraFallback()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        // Both years are closed and neither covers "today", so GetDefaultFiscalYearAsync
        // returns null (no year covers today, no open years exist) and the extension
        // must fall through to extraFallback instead of candidates.FirstOrDefault().
        var closedYear = _f.CreateFiscalYear("Closed", today.AddYears(-3), today.AddYears(-3).AddMonths(11), isClosed: true);
        var fallbackYear = _f.CreateFiscalYear("Fallback", today.AddYears(-2), today.AddYears(-2).AddMonths(11), isClosed: true);
        var candidates = new List<FiscalYear> { closedYear, fallbackYear };
        var ctx = new FiscalYearSelectionContext();

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates, extraFallback: fallbackYear);

        Assert.NotNull(seed);
        Assert.Equal(fallbackYear.Id, seed.Id);
    }

    [Fact]
    public async Task NoDefaultNoExtraFallback_FallsBackToFirstCandidate()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var closedYear = _f.CreateFiscalYear("Closed", today.AddYears(-3), today.AddYears(-3).AddMonths(11), isClosed: true);
        var candidates = new List<FiscalYear> { closedYear };
        var ctx = new FiscalYearSelectionContext();

        var seed = await ctx.ResolveSeedAsync(_f.FiscalYearService, candidates);

        Assert.NotNull(seed);
        Assert.Equal(closedYear.Id, seed.Id);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~FiscalYearSelectionContextResolveSeedTests"`
Expected: FAIL — `ResolveSeedAsync` doesn't exist (compile error).

- [ ] **Step 3: Implement**

Create `src/KoalaBooks.Domain/FiscalYearSelectionContextExtensions.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Domain;

public static class FiscalYearSelectionContextExtensions
{
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
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~FiscalYearSelectionContextResolveSeedTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/FiscalYearSelectionContextExtensions.cs tests/KoalaBooks.Tests/FiscalYearSelectionContextResolveSeedTests.cs
git commit -m "Add ResolveSeedAsync extension for shared fiscal-year seed resolution (#308)"
```

---

### Task 3: `TrialBalance.razor` — adopt the shared component

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/TrialBalance.razor:12-20,78-92`

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1), `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2).

- [ ] **Step 1: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/TrialBalance.razor`, replace lines 12-20:

```razor
    <div class="toolbar">
        <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
        <select @bind="SelectedFiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:200px;">
            @foreach (var fy in _fiscalYears)
            {
                <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
            }
        </select>
    </div>
```

with:

```razor
    <div class="toolbar">
        <FiscalYearSelector FiscalYears="_fiscalYears" @bind-SelectedFiscalYearId="SelectedFiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" />
    </div>
```

- [ ] **Step 2: Replace the seed-resolution block**

Replace lines 78-92:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync() ?? _fiscalYears.FirstOrDefault();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _fiscalYears);

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

- [ ] **Step 3: Build and run the existing test file**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~TrialBalancePageTests"`
Expected: PASS (unmodified — verifies `cut.Find("select")` still resolves through the nested component).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/TrialBalance.razor
git commit -m "TrialBalance.razor: adopt shared FiscalYearSelector and ResolveSeedAsync (#308)"
```

---

### Task 4: `BalanceSheet.razor` — adopt the shared component

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/BalanceSheet.razor:12-20,108-122`

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1), `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2).

- [ ] **Step 1: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/BalanceSheet.razor`, replace lines 12-20:

```razor
    <div class="toolbar">
        <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
        <select @bind="SelectedFiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:200px;">
            @foreach (var fy in _fiscalYears)
            {
                <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
            }
        </select>
    </div>
```

with:

```razor
    <div class="toolbar">
        <FiscalYearSelector FiscalYears="_fiscalYears" @bind-SelectedFiscalYearId="SelectedFiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" />
    </div>
```

- [ ] **Step 2: Replace the seed-resolution block**

Replace lines 108-122:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync() ?? _fiscalYears.FirstOrDefault();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _fiscalYears);

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

- [ ] **Step 3: Build and run the existing test file**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~BalanceSheetPageTests"`
Expected: PASS (unmodified).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/BalanceSheet.razor
git commit -m "BalanceSheet.razor: adopt shared FiscalYearSelector and ResolveSeedAsync (#308)"
```

---

### Task 5: `IncomeStatement.razor` — adopt the shared component

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/IncomeStatement.razor:13-19,100-114`

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1), `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2).

- [ ] **Step 1: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/IncomeStatement.razor`, replace lines 13-19:

```razor
        <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
        <select @bind="SelectedFiscalYearId" @bind:after="OnFiscalYearChanged" style="width:200px;">
            @foreach (var fy in _fiscalYears)
            {
                <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
            }
        </select>
```

with:

```razor
        <FiscalYearSelector FiscalYears="_fiscalYears" @bind-SelectedFiscalYearId="SelectedFiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChanged" />
```

- [ ] **Step 2: Replace the seed-resolution block**

Replace lines 100-114:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync() ?? _fiscalYears.FirstOrDefault();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _fiscalYears);

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

- [ ] **Step 3: Build and run the existing test file**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~IncomeStatementPageTests"`
Expected: PASS (unmodified).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/IncomeStatement.razor
git commit -m "IncomeStatement.razor: adopt shared FiscalYearSelector and ResolveSeedAsync (#308)"
```

---

### Task 6: `VatReport.razor` — adopt the shared component

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/VatReport.razor:17-23,172-186`

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1), `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2).

- [ ] **Step 1: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/VatReport.razor`, replace lines 17-23 (inside the existing `<div class="toolbar no-print" ...>` wrapper, which stays unchanged):

```razor
        <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
        <select @bind="SelectedFiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:200px;">
            @foreach (var fy in _fiscalYears)
            {
                <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
            }
        </select>
```

with:

```razor
        <FiscalYearSelector FiscalYears="_fiscalYears" @bind-SelectedFiscalYearId="SelectedFiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" />
```

- [ ] **Step 2: Replace the seed-resolution block**

Replace lines 172-186:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync() ?? _fiscalYears.FirstOrDefault();

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _fiscalYears = await FiscalYearService.GetAllAsync();

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _fiscalYears);

        if (seed is not null)
        {
            SelectedFiscalYearId = seed.Id;
            await LoadReport();
        }
    }
```

- [ ] **Step 3: Build and run the existing test file**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~VatReportPageTests"`
Expected: PASS (unmodified).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/VatReport.razor
git commit -m "VatReport.razor: adopt shared FiscalYearSelector and ResolveSeedAsync (#308)"
```

---

### Task 7: `GeneralLedger.razor` — adopt the shared component

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/GeneralLedger.razor:18-23,194-216`

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1), `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2).

- [ ] **Step 1: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/GeneralLedger.razor`, replace lines 18-23 (inside the existing `<div class="toolbar" style="flex-wrap:wrap; gap:0.75rem;">` wrapper, which stays unchanged, and keeps the "Sök konto:" field that follows):

```razor
        <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
        <select @bind="SelectedFiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:200px;">
            @foreach (var fy in _fiscalYears)
            {
                <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
            }
        </select>
```

with:

```razor
        <FiscalYearSelector FiscalYears="_fiscalYears" @bind-SelectedFiscalYearId="SelectedFiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" />
```

- [ ] **Step 2: Replace the seed-resolution block**

Replace lines 194-216:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        try
        {
            _fiscalYears = await FiscalYearService.GetAllAsync();

            FiscalYear? seed = null;
            if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
                seed = _fiscalYears.FirstOrDefault(f => f.Id == lastId);
            seed ??= await FiscalYearService.GetDefaultFiscalYearAsync() ?? _fiscalYears.FirstOrDefault();

            if (seed is not null)
            {
                SelectedFiscalYearId = seed.Id;
                await LoadAccountList();
            }
        }
        finally
        {
            _isLoading = false;
        }
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        try
        {
            _fiscalYears = await FiscalYearService.GetAllAsync();

            var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _fiscalYears);

            if (seed is not null)
            {
                SelectedFiscalYearId = seed.Id;
                await LoadAccountList();
            }
        }
        finally
        {
            _isLoading = false;
        }
    }
```

- [ ] **Step 3: Build and run the existing test file**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~GeneralLedgerPageTests"`
Expected: PASS (unmodified).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/GeneralLedger.razor
git commit -m "GeneralLedger.razor: adopt shared FiscalYearSelector and ResolveSeedAsync (#308)"
```

---

### Task 8: `Journal.razor` — adopt the shared component (with `extraFallback`)

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:29-34,371-390`

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1) with `Width="220px"`, `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2) with `extraFallback: _activeFiscalYear` — preserves Journal's existing fallback chain (last-selected → default → latest open → latest overall).

- [ ] **Step 1: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/Journal.razor`, replace lines 29-34:

```razor
    <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
    <select @bind="_selectedFiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:220px;">
        @foreach (var fy in _allFiscalYears)
        {
            <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
        }
    </select>
```

with:

```razor
    <FiscalYearSelector FiscalYears="_allFiscalYears" @bind-SelectedFiscalYearId="_selectedFiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" Width="220px" />
```

- [ ] **Step 2: Replace the seed-resolution block**

Replace lines 371-390:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _allFiscalYears = await FiscalYearService.GetAllAsync();
        _activeFiscalYear = _allFiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _allFiscalYears.FirstOrDefault();
        if (!_allFiscalYears.Any())
        {
            _isLoading = false;
            return;
        }

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _allFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync() ?? _activeFiscalYear ?? _allFiscalYears.First();

        _selectedFiscalYearId = seed.Id;
        await LoadForSelectedYearAsync();
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _allFiscalYears = await FiscalYearService.GetAllAsync();
        _activeFiscalYear = _allFiscalYears.FirstOrDefault(f => !f.IsClosed) ?? _allFiscalYears.FirstOrDefault();
        if (!_allFiscalYears.Any())
        {
            _isLoading = false;
            return;
        }

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _allFiscalYears, _activeFiscalYear);

        _selectedFiscalYearId = seed!.Id;
        await LoadForSelectedYearAsync();
        _isLoading = false;
    }
```

(`seed!` is safe: `_allFiscalYears` is non-empty at this point, guaranteed by the early return above, so `ResolveSeedAsync`'s `candidates.FirstOrDefault()` tail always yields a non-null result.)

- [ ] **Step 3: Build and run the existing test file**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~JournalPageTests"`
Expected: PASS (unmodified).

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "Journal.razor: adopt shared FiscalYearSelector and ResolveSeedAsync (#308)"
```

---

### Task 9: `Accounts.razor` — adopt the shared component + new component tests

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Accounts.razor:25-30,249-271`
- Test: `tests/KoalaBooks.ComponentTests/AccountsPageTests.cs` (new)

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1) with `Width="220px"`, `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2), no `extraFallback`.
- **Behavior change under test:** previously `seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();` had no further fallback — if no default fiscal year existed, `_activeFiscalYear` stayed `null` and the page showed "Inget aktivt räkenskapsår hittades." `ResolveSeedAsync`'s `?? candidates.FirstOrDefault()` tail now seeds the first open year instead.

- [ ] **Step 1: Write the failing component tests**

Create `tests/KoalaBooks.ComponentTests/AccountsPageTests.cs`:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Closes the #308 zero-component-test-coverage gap for Accounts, and covers the extraction's
// intentional behavior change: Accounts now falls back to the first open fiscal year when
// there is no shared selection and no default fiscal year set (previously it showed nothing).
public class AccountsPageTests : BunitContext
{
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IBasImportService _basImportService = Substitute.For<IBasImportService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    // Both open; Accounts orders its own open-year filter by StartDate descending, so
    // OpenFyNewer sorts first and OpenFyOlder sorts second.
    private static readonly FiscalYear OpenFyOlder = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = false };
    private static readonly FiscalYear OpenFyNewer = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public AccountsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        _fiscalYearService.GetAllAsync().Returns([OpenFyOlder, OpenFyNewer]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);

        Services.AddSingleton(_accountService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_basImportService);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_EvenWhenNotTheDefault()
    {
        _selectionContext.Set(OpenFyOlder.Id);
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);

        Render<Accounts>();

        await _accountService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyOlder);

        Render<Accounts>();

        await _accountService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task NoSharedSelectionAndNoDefault_FallsBackToFirstOpenCandidate()
    {
        // Regression for the #308 behavior change described above.
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns((FiscalYear?)null);

        Render<Accounts>();

        await _accountService.Received(1).GetAllAsync(OpenFyNewer.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);
        var cut = Render<Accounts>();

        cut.Find("select").Change(OpenFyOlder.Id.ToString());

        Assert.Equal(OpenFyOlder.Id, _selectionContext.LastSelectedFiscalYearId);
        await _accountService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~AccountsPageTests"`
Expected: FAIL — `NoSharedSelectionAndNoDefault_FallsBackToFirstOpenCandidate` fails because `Accounts.razor` still has no fallback tail (`_accountService` never receives a call, or `Render<Accounts>()` shows the "no active fiscal year" alert instead).

- [ ] **Step 3: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/Accounts.razor`, replace lines 25-30:

```razor
    <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
    <select @bind="_fiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:220px;">
        @foreach (var fy in _openFiscalYears)
        {
            <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
        }
    </select>
```

with:

```razor
    <FiscalYearSelector FiscalYears="_openFiscalYears" @bind-SelectedFiscalYearId="_fiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" Width="220px" />
```

- [ ] **Step 4: Replace the seed-resolution block**

Replace lines 249-271:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        var allYears = await FiscalYearService.GetAllAsync();
        _openFiscalYears = allYears.Where(f => !f.IsClosed).OrderByDescending(f => f.StartDate).ToList();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        _activeFiscalYear = seed;
        _otherFiscalYears = allYears
            .Where(f => f.Id != _activeFiscalYear?.Id)
            .OrderByDescending(f => f.StartDate)
            .ToList();
        if (_activeFiscalYear is not null)
        {
            _fiscalYearId = _activeFiscalYear.Id;
            await LoadAccounts();
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        var allYears = await FiscalYearService.GetAllAsync();
        _openFiscalYears = allYears.Where(f => !f.IsClosed).OrderByDescending(f => f.StartDate).ToList();

        _activeFiscalYear = await SelectionContext.ResolveSeedAsync(FiscalYearService, _openFiscalYears);

        _otherFiscalYears = allYears
            .Where(f => f.Id != _activeFiscalYear?.Id)
            .OrderByDescending(f => f.StartDate)
            .ToList();
        if (_activeFiscalYear is not null)
        {
            _fiscalYearId = _activeFiscalYear.Id;
            await LoadAccounts();
        }
        _isLoading = false;
    }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~AccountsPageTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Accounts.razor tests/KoalaBooks.ComponentTests/AccountsPageTests.cs
git commit -m "Accounts.razor: adopt shared FiscalYearSelector and ResolveSeedAsync, add component tests (#308)"
```

---

### Task 10: `BankImport.razor` — adopt the shared component + new component tests

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/BankImport.razor:26-31,483-499`
- Test: `tests/KoalaBooks.ComponentTests/BankImportPageTests.cs` (new)

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1) with `Width="220px"`, `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2), no `extraFallback`.

- [ ] **Step 1: Write the failing component tests**

Create `tests/KoalaBooks.ComponentTests/BankImportPageTests.cs`:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Closes the #308 zero-component-test-coverage gap for BankImport (route "/import/bank").
public class BankImportPageTests : BunitContext
{
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IBankImportService _bankImportService = Substitute.For<IBankImportService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly IJournalEntryService _journalEntryService = Substitute.For<IJournalEntryService>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear OpenFyOlder = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = false };
    private static readonly FiscalYear OpenFyNewer = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public BankImportPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        // GetOpenFiscalYearsAsync is already ordered by StartDate descending by the real service.
        _fiscalYearService.GetOpenFiscalYearsAsync().Returns([OpenFyNewer, OpenFyOlder]);
        _bankImportService.GetImportableAccountsAsync(Arg.Any<int>(), Arg.Any<string>()).Returns([]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);

        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_bankImportService);
        Services.AddSingleton(_accountService);
        Services.AddSingleton(_journalEntryService);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_EvenWhenNotTheDefault()
    {
        _selectionContext.Set(OpenFyOlder.Id);
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);

        Render<BankImport>();

        await _bankImportService.Received(1).GetImportableAccountsAsync(OpenFyOlder.Id, "19");
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyOlder);

        Render<BankImport>();

        await _bankImportService.Received(1).GetImportableAccountsAsync(OpenFyOlder.Id, "19");
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);
        var cut = Render<BankImport>();

        cut.Find("select").Change(OpenFyOlder.Id.ToString());

        Assert.Equal(OpenFyOlder.Id, _selectionContext.LastSelectedFiscalYearId);
        await _bankImportService.Received(1).GetImportableAccountsAsync(OpenFyOlder.Id, "19");
    }
}
```

- [ ] **Step 2: Run to verify current behavior**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~BankImportPageTests"`
Expected: PASS already (the page's current behavior already satisfies these three cases) — this task is about consolidating the markup/seed logic, not changing behavior, so no red step is expected here beyond confirming the new file compiles and passes against the pre-refactor page.

- [ ] **Step 3: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/BankImport.razor`, replace lines 26-31:

```razor
    <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
    <select @bind="_fiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:220px;">
        @foreach (var fy in _openFiscalYears)
        {
            <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
        }
    </select>
```

with:

```razor
    <FiscalYearSelector FiscalYears="_openFiscalYears" @bind-SelectedFiscalYearId="_fiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" Width="220px" />
```

- [ ] **Step 4: Replace the seed-resolution block**

Replace lines 483-499:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _openFiscalYears);

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~BankImportPageTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/BankImport.razor tests/KoalaBooks.ComponentTests/BankImportPageTests.cs
git commit -m "BankImport.razor: adopt shared FiscalYearSelector and ResolveSeedAsync, add component tests (#308)"
```

---

### Task 11: `CustomerInvoices.razor` — adopt the shared component + new component tests

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/CustomerInvoices.razor:32-37,494-510`
- Test: `tests/KoalaBooks.ComponentTests/CustomerInvoicesPageTests.cs` (new)

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1) with `Width="220px"`, `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2), no `extraFallback`.

- [ ] **Step 1: Write the failing component tests**

Create `tests/KoalaBooks.ComponentTests/CustomerInvoicesPageTests.cs`:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Closes the #308 zero-component-test-coverage gap for CustomerInvoices.
public class CustomerInvoicesPageTests : BunitContext
{
    private readonly ICustomerInvoiceService _invoiceService = Substitute.For<ICustomerInvoiceService>();
    private readonly ICustomerService _customerService = Substitute.For<ICustomerService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();
    private readonly IDocumentProvider _documentProvider = Substitute.For<IDocumentProvider>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear OpenFyOlder = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = false };
    private static readonly FiscalYear OpenFyNewer = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public CustomerInvoicesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        _fiscalYearService.GetOpenFiscalYearsAsync().Returns([OpenFyNewer, OpenFyOlder]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _customerService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _invoiceService.GetAllAsync(Arg.Any<int>()).Returns([]);

        Services.AddSingleton(_invoiceService);
        Services.AddSingleton(_customerService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_accountService);
        Services.AddSingleton(_documentService);
        Services.AddSingleton(_documentProvider);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_EvenWhenNotTheDefault()
    {
        _selectionContext.Set(OpenFyOlder.Id);
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);

        Render<CustomerInvoices>();

        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyOlder);

        Render<CustomerInvoices>();

        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);
        var cut = Render<CustomerInvoices>();

        cut.Find("select").Change(OpenFyOlder.Id.ToString());

        Assert.Equal(OpenFyOlder.Id, _selectionContext.LastSelectedFiscalYearId);
        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }
}
```

- [ ] **Step 2: Run to verify current behavior**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~CustomerInvoicesPageTests"`
Expected: PASS already (the page's current behavior already satisfies these three cases) — this task is about consolidating the markup/seed logic, not changing behavior, so no red step is expected here beyond confirming the new file compiles and passes against the pre-refactor page.

- [ ] **Step 3: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`, replace lines 32-37:

```razor
    <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
    <select @bind="_fiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:220px;">
        @foreach (var fy in _openFiscalYears)
        {
            <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
        }
    </select>
```

with:

```razor
    <FiscalYearSelector FiscalYears="_openFiscalYears" @bind-SelectedFiscalYearId="_fiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" Width="220px" />
```

- [ ] **Step 4: Replace the seed-resolution block**

Replace lines 494-510:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _openFiscalYears);

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~CustomerInvoicesPageTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/CustomerInvoices.razor tests/KoalaBooks.ComponentTests/CustomerInvoicesPageTests.cs
git commit -m "CustomerInvoices.razor: adopt shared FiscalYearSelector and ResolveSeedAsync, add component tests (#308)"
```

---

### Task 12: `SupplierInvoices.razor` — adopt the shared component + new component tests

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/SupplierInvoices.razor:32-37,472-488`
- Test: `tests/KoalaBooks.ComponentTests/SupplierInvoicesPageTests.cs` (new)

**Interfaces:**
- Consumes: `FiscalYearSelector` (Task 1) with `Width="220px"`, `FiscalYearSelectionContext.ResolveSeedAsync` (Task 2), no `extraFallback`.

- [ ] **Step 1: Write the failing component tests**

Create `tests/KoalaBooks.ComponentTests/SupplierInvoicesPageTests.cs`:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Components.Pages;
using KoalaBooks.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KoalaBooks.ComponentTests;

// Closes the #308 zero-component-test-coverage gap for SupplierInvoices.
public class SupplierInvoicesPageTests : BunitContext
{
    private readonly ISupplierInvoiceService _invoiceService = Substitute.For<ISupplierInvoiceService>();
    private readonly IFiscalYearService _fiscalYearService = Substitute.For<IFiscalYearService>();
    private readonly IAccountService _accountService = Substitute.For<IAccountService>();
    private readonly IDocumentService _documentService = Substitute.For<IDocumentService>();
    private readonly IDocumentProvider _documentProvider = Substitute.For<IDocumentProvider>();
    private readonly FiscalYearSelectionContext _selectionContext = new();

    private static readonly FiscalYear OpenFyOlder = new() { Id = 1, Name = "2025", StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31), IsClosed = false };
    private static readonly FiscalYear OpenFyNewer = new() { Id = 2, Name = "2026", StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31), IsClosed = false };

    public SupplierInvoicesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();

        _fiscalYearService.GetOpenFiscalYearsAsync().Returns([OpenFyNewer, OpenFyOlder]);
        _accountService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _invoiceService.GetAllAsync(Arg.Any<int>()).Returns([]);
        _invoiceService.GetSuppliersAsync(Arg.Any<int>()).Returns([]);

        Services.AddSingleton(_invoiceService);
        Services.AddSingleton(_fiscalYearService);
        Services.AddSingleton(_accountService);
        Services.AddSingleton(_documentService);
        Services.AddSingleton(_documentProvider);
        Services.AddSingleton(_selectionContext);
    }

    [Fact]
    public async Task SeedsFromSharedSelection_EvenWhenNotTheDefault()
    {
        _selectionContext.Set(OpenFyOlder.Id);
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);

        Render<SupplierInvoices>();

        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task FallsBackToDefault_WhenNoSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyOlder);

        Render<SupplierInvoices>();

        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }

    [Fact]
    public async Task ChangingFiscalYear_WritesBackToSharedSelection()
    {
        _fiscalYearService.GetDefaultFiscalYearAsync().Returns(OpenFyNewer);
        var cut = Render<SupplierInvoices>();

        cut.Find("select").Change(OpenFyOlder.Id.ToString());

        Assert.Equal(OpenFyOlder.Id, _selectionContext.LastSelectedFiscalYearId);
        await _invoiceService.Received(1).GetAllAsync(OpenFyOlder.Id);
    }
}
```

- [ ] **Step 2: Run to verify current behavior**

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~SupplierInvoicesPageTests"`
Expected: PASS already against the pre-refactor page — confirms the new file compiles and the three cases hold before the markup/seed-logic swap; Step 3/4 consolidate the implementation without changing this behavior.

- [ ] **Step 3: Replace the toolbar markup**

In `src/KoalaBooks.Components/Pages/SupplierInvoices.razor`, replace lines 32-37:

```razor
    <label style="font-weight:600; color:#475569;">Räkenskapsår:</label>
    <select @bind="_fiscalYearId" @bind:after="OnFiscalYearChangedAsync" style="width:220px;">
        @foreach (var fy in _openFiscalYears)
        {
            <option value="@fy.Id">@fy.Name (@fy.StartDate — @fy.EndDate)</option>
        }
    </select>
```

with:

```razor
    <FiscalYearSelector FiscalYears="_openFiscalYears" @bind-SelectedFiscalYearId="_fiscalYearId" @bind-SelectedFiscalYearId:after="OnFiscalYearChangedAsync" Width="220px" />
```

- [ ] **Step 4: Replace the seed-resolution block**

Replace lines 472-488:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        FiscalYear? seed = null;
        if (SelectionContext.LastSelectedFiscalYearId is { } lastId)
            seed = _openFiscalYears.FirstOrDefault(f => f.Id == lastId);
        seed ??= await FiscalYearService.GetDefaultFiscalYearAsync();

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }
```

with:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _openFiscalYears = await FiscalYearService.GetOpenFiscalYearsAsync();

        var seed = await SelectionContext.ResolveSeedAsync(FiscalYearService, _openFiscalYears);

        if (seed is not null)
        {
            _fiscalYearId = seed.Id;
            await LoadForSelectedYearAsync();
        }
        _isLoading = false;
    }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test tests/KoalaBooks.ComponentTests --filter "FullyQualifiedName~SupplierInvoicesPageTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/SupplierInvoices.razor tests/KoalaBooks.ComponentTests/SupplierInvoicesPageTests.cs
git commit -m "SupplierInvoices.razor: adopt shared FiscalYearSelector and ResolveSeedAsync, add component tests (#308)"
```

---

### Task 13: Full verification pass

**Files:** None (verification only).

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Full test suite**

Run: `dotnet test`
Expected: All tests PASS, including all 12 fiscal-year-selector-related files (6 pre-existing + 1 new component + 1 new extension unit test + 4 new open-years-page test files) and every other unaffected test in the solution.

- [ ] **Step 3: Manual verification**

Start the app (`aspire start --isolated` + playwright-cli, or the project's `run` skill). Using client-side navigation only (goto tears down the Blazor Server circuit — see the existing gotcha), verify on 2-3 representative pages:
- One open-years page (e.g. Accounts): selector shows `width:220px`, selecting a year updates the account list and persists across navigation to another open-years page.
- One all-years report page (e.g. TrialBalance): selector shows `width:200px`, selecting a year reloads the report and the same selection appears pre-selected on GeneralLedger.
- Journal: confirms its extra fallback still resolves to the latest open year when no default/shared selection exists.

- [ ] **Step 4: No commit** — this task is verification-only; if any check fails, return to the relevant task above, fix, and re-commit there.
