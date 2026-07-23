# Journal Entries Pagination Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `JournalEntriesController.GetByFiscalYear`'s in-memory materialize-then-slice pattern with DB-pushed-down `OFFSET`/`LIMIT` pagination (via a new `IJournalEntryService.GetByFiscalYearAsync` signature), and give `Journal.razor` a real paged/sorted/filtered UI.

**Architecture:** `IJournalEntryService.GetByFiscalYearAsync` changes from `Task<List<JournalEntry>>` to `Task<PagedResult<JournalEntry>>` and gains `search`/`sortBy`/`page`/`pageSize` parameters. The EF implementation (`JournalEntryService`) pushes `Where`/`OrderBy`/`Skip`/`Take` into the SQL query instead of materializing then slicing in .NET. Every caller across Domain/Application/Client(WASM)/Web/Components is updated to the new signature — there is no "get everything" escape hatch.

**Tech Stack:** .NET, EF Core (Npgsql), ASP.NET Core Web API, Blazor (Server + WASM/InteractiveAuto), MudBlazor 9.7.0, xUnit + Testcontainers Postgres, bUnit + NSubstitute.

## Global Constraints

- Full design rationale lives in `docs/superpowers/specs/2026-07-22-journal-entries-cursor-pagination-design.md` — this is DB-level offset pagination, not keyset/cursor pagination, despite issue #343's title (see spec's "Why not true keyset/cursor pagination" section).
- No "get everything" helper is added anywhere — every caller of `GetByFiscalYearAsync` must work against a single bounded page.
- `pageSize` stays server-clamped to 1–200 in the controller (unchanged); `page` stays clamped to a minimum of 1 (unchanged). The UI's 25/50/100 selector is a separate, tighter constraint that does not change the wire-level clamp.
- Solution project dependency order is: `KoalaBooks.Domain` ← `KoalaBooks.Application`, `KoalaBooks.Infrastructure`, `KoalaBooks.Components` ← `KoalaBooks.Client` ← `KoalaBooks.Web`. Because the interface signature changes, **the full solution will not compile until Task 5 lands** — this is expected for a cross-project signature-propagation refactor, not a mistake. Each task below states exactly which project(s) are independently buildable/testable at that point; don't be alarmed when `dotnet build`/`dotnet test` on the whole solution fails before Task 5.
- `tests/KoalaBooks.Tests` (xUnit, Testcontainers Postgres via `WebApiFactory`/`TestFixture`) references `KoalaBooks.Web` transitively, so it only compiles once Task 5 lands. `tests/KoalaBooks.ComponentTests` (bUnit) references only `Application`+`Components`+`Domain`, so it becomes buildable/runnable after Task 3.

---

### Task 1: Domain — `PagedResult<T>`, `JournalEntrySortBy`, and the `IJournalEntryService` signature change

**Files:**
- Create: `src/KoalaBooks.Domain/Enums/JournalEntrySortBy.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/PagedResult.cs`
- Modify: `src/KoalaBooks.Domain/Interfaces/IJournalEntryService.cs`

**Interfaces:**
- Produces: `KoalaBooks.Domain.Enums.JournalEntrySortBy` (values `EntryNumber`, `Date`), `KoalaBooks.Domain.Interfaces.PagedResult<T>` (`Items`/`Page`/`PageSize`/`TotalCount`), and the new `IJournalEntryService.GetByFiscalYearAsync` signature:
  ```csharp
  Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
      int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
      string? search = null,
      JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
      int page = 1, int pageSize = 50);
  ```
  Every later task consumes this exact signature — do not rename parameters.

This task intentionally breaks compilation of every other project (`Application`, `Client`, `Web`, `Components` all call the old signature). That's expected — Task 2–5 fix each project in dependency order. Only `KoalaBooks.Domain` itself is verified buildable here.

- [ ] **Step 1: Create the `JournalEntrySortBy` enum**

```csharp
// src/KoalaBooks.Domain/Enums/JournalEntrySortBy.cs
namespace KoalaBooks.Domain.Enums;

public enum JournalEntrySortBy
{
    EntryNumber,
    Date
}
```

- [ ] **Step 2: Create the `PagedResult<T>` type**

```csharp
// src/KoalaBooks.Domain/Interfaces/PagedResult.cs
namespace KoalaBooks.Domain.Interfaces;

public class PagedResult<T>
{
    public List<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
}
```

- [ ] **Step 3: Change `IJournalEntryService.GetByFiscalYearAsync`'s signature**

In `src/KoalaBooks.Domain/Interfaces/IJournalEntryService.cs`, add `using KoalaBooks.Domain.Enums;` to the usings, and replace:

```csharp
    Task<List<JournalEntry>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null);
```

with:

```csharp
    Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        string? search = null,
        JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
        int page = 1, int pageSize = 50);
```

The full file should read:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Interfaces;

public interface IJournalEntryService
{
    Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        string? search = null,
        JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
        int page = 1, int pageSize = 50);
    Task<int> CountDraftsAsync(int fiscalYearId);
    Task<List<JournalEntry>> GetDraftsForOrganisationAsync();
    Task<int> CountDraftsForOrganisationAsync();
    Task<JournalEntry?> GetByIdAsync(int id);
    Task<(JournalEntry? Entry, string? Error)> CreateAsync(JournalEntry entry);
    Task<(List<JournalEntry> Created, string? Error, int? FailedEntryIndex)> CreateManyAsync(int fiscalYearId, List<JournalEntry> entries);
    Task<(JournalEntry? Entry, string? Error)> UpdateAsync(JournalEntry entry);
    Task<string?> PostAsync(int entryId);
    Task<string?> DeleteDraftAsync(int entryId);
    Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason);
    Task<(JournalEntry? Preview, string? Error)> PreviewReversalAsync(int entryId, string reason);
}
```

- [ ] **Step 4: Verify `KoalaBooks.Domain` builds standalone**

Run: `dotnet build src/KoalaBooks.Domain/KoalaBooks.Domain.csproj`
Expected: `Build succeeded.` (This project has no dependency on the callers you're about to break, so it must be green here.)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Enums/JournalEntrySortBy.cs src/KoalaBooks.Domain/Interfaces/PagedResult.cs src/KoalaBooks.Domain/Interfaces/IJournalEntryService.cs
git commit -m "Add PagedResult<T>/JournalEntrySortBy and repoint IJournalEntryService.GetByFiscalYearAsync at them"
```

---

### Task 2: Application — push pagination into `JournalEntryService.GetByFiscalYearAsync`

**Files:**
- Modify: `src/KoalaBooks.Application/Services/JournalEntryService.cs` (the `GetByFiscalYearAsync` method only, lines ~26-38 today)
- Modify: `tests/KoalaBooks.Tests/TenantIsolationTests.cs` (~line 117 — this file won't compile as part of a full solution build until Task 5, but fix it now for locality with the signature change)

**Interfaces:**
- Consumes: `PagedResult<JournalEntry>`, `JournalEntrySortBy` from Task 1.
- Produces: no new public surface — this is the concrete implementation behind the interface Task 1 already declared.

- [ ] **Step 1: Replace the method body**

In `src/KoalaBooks.Application/Services/JournalEntryService.cs`, replace:

```csharp
    public async Task<List<JournalEntry>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.FiscalYearId == fiscalYearId);

        if (from.HasValue)
            query = query.Where(j => j.Date >= from.Value);
        if (to.HasValue)
            query = query.Where(j => j.Date <= to.Value);

        return await query.OrderBy(j => j.EntryNumber).ToListAsync().ConfigureAwait(false);
    }
```

with:

```csharp
    public async Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        string? search = null,
        JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
        int page = 1, int pageSize = 50)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.FiscalYearId == fiscalYearId);

        if (from.HasValue)
            query = query.Where(j => j.Date >= from.Value);
        if (to.HasValue)
            query = query.Where(j => j.Date <= to.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = int.TryParse(s, out var entryNumber)
                ? query.Where(j => j.EntryNumber == entryNumber || EF.Functions.ILike(j.Description, $"%{s}%"))
                : query.Where(j => EF.Functions.ILike(j.Description, $"%{s}%"));
        }

        query = sortBy switch
        {
            JournalEntrySortBy.Date => query.OrderBy(j => j.Date).ThenBy(j => j.EntryNumber),
            _ => query.OrderBy(j => j.EntryNumber)
        };

        var totalCount = await query.CountAsync().ConfigureAwait(false);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync().ConfigureAwait(false);

        return new PagedResult<JournalEntry>
        {
            Items = items, Page = page, PageSize = pageSize, TotalCount = totalCount
        };
    }
```

`using KoalaBooks.Domain.Interfaces;` and `using KoalaBooks.Domain.Enums;` are already present at the top of this file — no new usings needed.

- [ ] **Step 2: Verify `KoalaBooks.Application` builds standalone**

Run: `dotnet build src/KoalaBooks.Application/KoalaBooks.Application.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Fix the one direct caller in `TenantIsolationTests.cs`**

In `tests/KoalaBooks.Tests/TenantIsolationTests.cs`, in `GetJournalEntriesByFiscalYear_AsOtherTenant_ReturnsEmpty`, replace:

```csharp
        var results = await service.GetByFiscalYearAsync(fyA.Id);

        Assert.Empty(results);
```

with:

```csharp
        var results = await service.GetByFiscalYearAsync(fyA.Id);

        Assert.Empty(results.Items);
```

This file can't be built/run in isolation yet (see Global Constraints) — this edit just keeps the source consistent as the signature propagates. It will be exercised for real in Task 5's verification step.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Application/Services/JournalEntryService.cs tests/KoalaBooks.Tests/TenantIsolationTests.cs
git commit -m "Push GetByFiscalYearAsync pagination/search/sort down into the EF query"
```

---

### Task 3: Components — `Journal.razor` real pagination UI, `ClassifyDocumentDialog.razor` autocomplete picker, and `JournalPageTests.cs` fixes

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`
- Modify: `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor`
- Modify: `tests/KoalaBooks.ComponentTests/JournalPageTests.cs`

**Interfaces:**
- Consumes: `IJournalEntryService.GetByFiscalYearAsync(int, DateOnly?, DateOnly?, string?, JournalEntrySortBy, int, int)` returning `PagedResult<JournalEntry>` (Task 1/2).
- Produces: no new public surface consumed by later tasks — this is the last layer touching `IJournalEntryService` before Client/Web.

This is the first task where a project outside Domain/Application becomes independently testable: `tests/KoalaBooks.ComponentTests` references only `Application`+`Components`+`Domain` (no `Web`/`Client`), so after this task `dotnet test tests/KoalaBooks.ComponentTests` should build and pass.

- [ ] **Step 1: Rewrite `Journal.razor`'s data-loading and add pagination/sort/page-size state**

In `src/KoalaBooks.Components/Pages/Journal.razor`'s `@code` block, replace the field block:

```csharp
    private string _selectedMonthStr = "";
    private int? SelectedMonth => string.IsNullOrEmpty(_selectedMonthStr) ? null : int.Parse(_selectedMonthStr);
    private IEnumerable<JournalEntry> FilteredEntries =>
        SelectedMonth.HasValue
            ? _entries.Where(e => e.Date.Month == SelectedMonth.Value)
            : _entries;
    private List<Account> _accounts = [];
    private List<JournalEntry> _entries = [];
```

with:

```csharp
    private string _selectedMonthStr = "";
    private int? SelectedMonth => string.IsNullOrEmpty(_selectedMonthStr) ? null : int.Parse(_selectedMonthStr);
    private List<Account> _accounts = [];
    private List<JournalEntry> _entries = [];
    private int _page = 1;
    private int _pageSize = 50;
    private JournalEntrySortBy _sortBy = JournalEntrySortBy.EntryNumber;
    private int _totalCount;
```

(`FilteredEntries` is removed — the month filter is now sent to the server as `from`/`to`, not applied client-side.)

Replace `LoadForSelectedYearAsync`, `ReloadEntriesAsync`, and the inline reload in `ConfirmConvert` (three near-duplicate call sites the design spec calls out) with a single `LoadEntriesAsync` plus a small date-range helper. Replace:

```csharp
    private async Task LoadForSelectedYearAsync()
    {
        _accounts = (await AccountService.GetAllAsync(_selectedFiscalYearId)).Where(a => a.IsActive).ToList();
        _entries = (await JournalEntryService.GetByFiscalYearAsync(_selectedFiscalYearId)).Where(e => e.IsPosted).ToList();
        _linkedJournalEntryIds = await InvoiceService.GetLinkedJournalEntryIdsAsync(_selectedFiscalYearId);
        _knownSuppliers = await InvoiceService.GetSuppliersAsync(_selectedFiscalYearId);
        _attachmentCounts = await DocumentService.GetCountsForJournalEntriesAsync(_entries.Select(e => e.Id));
    }
```

with:

```csharp
    private async Task LoadForSelectedYearAsync()
    {
        _accounts = (await AccountService.GetAllAsync(_selectedFiscalYearId)).Where(a => a.IsActive).ToList();
        await LoadEntriesAsync();
    }

    private async Task LoadEntriesAsync()
    {
        var range = SelectedMonthRange();
        var result = await JournalEntryService.GetByFiscalYearAsync(
            _selectedFiscalYearId, range?.From, range?.To,
            sortBy: _sortBy, page: _page, pageSize: _pageSize);
        _entries = result.Items.Where(e => e.IsPosted).ToList();
        _totalCount = result.TotalCount;
        _linkedJournalEntryIds = await InvoiceService.GetLinkedJournalEntryIdsAsync(_selectedFiscalYearId);
        _knownSuppliers = await InvoiceService.GetSuppliersAsync(_selectedFiscalYearId);
        _attachmentCounts = await DocumentService.GetCountsForJournalEntriesAsync(_entries.Select(e => e.Id));
    }

    private (DateOnly From, DateOnly To)? SelectedMonthRange()
    {
        if (!SelectedMonth.HasValue || _selectedFiscalYear is null) return null;
        var fy = _selectedFiscalYear;
        var current = new DateOnly(fy.StartDate.Year, fy.StartDate.Month, 1);
        var end = new DateOnly(fy.EndDate.Year, fy.EndDate.Month, 1);
        while (current <= end)
        {
            if (current.Month == SelectedMonth.Value)
                return (current, current.AddMonths(1).AddDays(-1));
            current = current.AddMonths(1);
        }
        return null;
    }

    private async Task OnFilterChangedAsync()
    {
        _page = 1;
        await LoadEntriesAsync();
    }

    private async Task GoToPageAsync(int page)
    {
        _page = page;
        await LoadEntriesAsync();
    }
```

`SelectedMonthRange` walks the fiscal year's own month sequence (same walk `AvailableMonths()` already does) rather than assuming a calendar year, so a split fiscal year (e.g. May–April) still resolves the selected month number to the correct concrete year.

Replace `ReloadEntriesAsync` (called after save/post and after a reversal):

```csharp
    private async Task ReloadEntriesAsync()
    {
        _entries = (await JournalEntryService.GetByFiscalYearAsync(_selectedFiscalYearId)).Where(e => e.IsPosted).ToList();
        _attachmentCounts = await DocumentService.GetCountsForJournalEntriesAsync(_entries.Select(e => e.Id));
    }
```

with:

```csharp
    private async Task ReloadEntriesAsync() => await LoadEntriesAsync();
```

In `OnFiscalYearChangedAsync`, add a page reset alongside the existing month-filter reset:

```csharp
    private async Task OnFiscalYearChangedAsync()
    {
        SelectionContext.Set(_selectedFiscalYearId);
        _selectedMonthStr = "";
        _page = 1;
        _showForm = false;
        _isDirty = false;
        _attachmentEntryId = null;
        _attachmentMeta = [];
        _convertingEntryId = null;
        _isReloading = true;
        await LoadForSelectedYearAsync();
        _isReloading = false;
    }
```

In `ConfirmConvert`, replace the inline reload:

```csharp
            Snackbar.Add($"Faktura för {result!.SupplierName} skapad och kopplad till verifikation.", Severity.Success);
            _convertingEntryId = null;
            _entries = (await JournalEntryService.GetByFiscalYearAsync(_selectedFiscalYearId)).Where(e => e.IsPosted).ToList();
            _linkedJournalEntryIds = await InvoiceService.GetLinkedJournalEntryIdsAsync(_selectedFiscalYearId);
            _knownSuppliers = await InvoiceService.GetSuppliersAsync(_selectedFiscalYearId);
```

with:

```csharp
            Snackbar.Add($"Faktura för {result!.SupplierName} skapad och kopplad till verifikation.", Severity.Success);
            _convertingEntryId = null;
            await LoadEntriesAsync();
```

- [ ] **Step 2: Add the sort/page-size selectors and wire the month filter to reload**

In the toolbar `<div>`, replace:

```html
    <label style="font-weight:600; color:#475569;">Period:</label>
    <select @bind="_selectedMonthStr" style="width:180px;">
        <option value="">Hela året</option>
        @foreach (var (month, label) in AvailableMonths())
        {
            <option value="@month">@label</option>
        }
    </select>
```

with:

```html
    <label style="font-weight:600; color:#475569;">Period:</label>
    <select @bind="_selectedMonthStr" @bind:after="OnFilterChangedAsync" style="width:180px;">
        <option value="">Hela året</option>
        @foreach (var (month, label) in AvailableMonths())
        {
            <option value="@month">@label</option>
        }
    </select>
    <label style="font-weight:600; color:#475569;">Sortering:</label>
    <select @bind="_sortBy" @bind:after="OnFilterChangedAsync" style="width:170px;">
        <option value="@JournalEntrySortBy.EntryNumber">Verifikationsnummer</option>
        <option value="@JournalEntrySortBy.Date">Datum</option>
    </select>
    <label style="font-weight:600; color:#475569;">Per sida:</label>
    <select @bind="_pageSize" @bind:after="OnFilterChangedAsync" style="width:90px;">
        <option value="25">25</option>
        <option value="50">50</option>
        <option value="100">100</option>
    </select>
```

- [ ] **Step 3: Replace `FilteredEntries` with `_entries` in the markup, and add pagination controls**

The `@foreach (var entry in FilteredEntries)` (line ~83) becomes `@foreach (var entry in _entries)`.

Replace the empty-state block:

```html
@if (!FilteredEntries.Any() && !_showForm)
{
    <MudAlert Severity="Severity.Info" Class="mt-4">
        @if (_entries.Any())
        {
            <span>Inga verifikationer för vald period.</span>
        }
        else
        {
            <span>Inga verifikationer ännu. @if (_activeFiscalYear is not null && _selectedFiscalYearId == _activeFiscalYear.Id) { <span>Klicka "Ny verifikation" för att skapa en.</span> }</span>
        }
    </MudAlert>
}
```

with (the distinction is now "nothing at all in the year" vs. "nothing matching the current page/filter", using `_totalCount` instead of the old locally-filtered list):

```html
@if (!_entries.Any() && !_showForm)
{
    <MudAlert Severity="Severity.Info" Class="mt-4">
        @if (_totalCount == 0)
        {
            <span>Inga verifikationer ännu. @if (_activeFiscalYear is not null && _selectedFiscalYearId == _activeFiscalYear.Id) { <span>Klicka "Ny verifikation" för att skapa en.</span> }</span>
        }
        else
        {
            <span>Inga verifikationer för vald period.</span>
        }
    </MudAlert>
}

@if (_totalCount > 0)
{
    var totalPages = (int)Math.Ceiling(_totalCount / (double)_pageSize);
    <div style="display:flex; align-items:center; gap:0.4rem; margin-top:0.75rem; flex-wrap:wrap;">
        <button class="btn btn-sm btn-secondary" disabled="@(_page <= 1)" @onclick="() => GoToPageAsync(_page - 1)">‹ Föregående</button>
        @for (var p = 1; p <= totalPages; p++)
        {
            var pageNum = p;
            <button class="btn btn-sm @(pageNum == _page ? "btn-primary" : "btn-secondary")" @onclick="() => GoToPageAsync(pageNum)">@pageNum</button>
        }
        <button class="btn btn-sm btn-secondary" disabled="@(_page >= totalPages)" @onclick="() => GoToPageAsync(_page + 1)">Nästa ›</button>
        <span style="color:#64748b; font-size:0.85rem;">@_totalCount verifikationer</span>
    </div>
}
```

- [ ] **Step 4: Convert `ClassifyDocumentDialog.razor`'s linkable-entry `<select>` to a searching `MudAutocomplete`**

In the `@code` block, remove:

```csharp
    private bool _loadingEntries;
```

and remove `_linkableEntries`:

```csharp
    private List<JournalEntry> _linkableEntries = [];
```

Add:

```csharp
    private JournalEntry? _existingEntry;
```

Replace `LoadExistingEntries`:

```csharp
    private async Task LoadExistingEntries()
    {
        _jeMode = "existing";
        if (_linkableEntries.Count > 0 || _fiscalYear is null) return;
        _loadingEntries = true;
        _linkableEntries = await JournalEntryService.GetByFiscalYearAsync(_fiscalYear.Id);
        _loadingEntries = false;
    }
```

with:

```csharp
    private void SwitchToExistingMode() => _jeMode = "existing";

    private async Task<IEnumerable<JournalEntry>> SearchExistingEntriesAsync(string? search, CancellationToken ct)
    {
        if (_fiscalYear is null) return [];
        var result = await JournalEntryService.GetByFiscalYearAsync(_fiscalYear.Id, search: search, page: 1, pageSize: 20);
        return result.Items;
    }

    private void OnExistingEntrySelected(JournalEntry? entry)
    {
        _existingEntry = entry;
        _existingEntryId = entry?.Id ?? 0;
        MarkDirty();
    }
```

Update the mode-switch button's `@onclick`:

```html
                        <button class="btn btn-sm @(_jeMode == "existing" ? "btn-primary" : "btn-secondary")"
                                @onclick="LoadExistingEntries">Koppla befintlig</button>
```

becomes:

```html
                        <button class="btn btn-sm @(_jeMode == "existing" ? "btn-primary" : "btn-secondary")"
                                @onclick="SwitchToExistingMode">Koppla befintlig</button>
```

Replace the whole `else` branch that rendered the `<select>` (including its loading/empty states):

```html
                    else
                    {
                        @if (_linkableEntries.Count == 0 && !_loadingEntries)
                        {
                            <p style="color:#94a3b8; font-size:0.85rem;">Inga verifikationer hittades.</p>
                        }
                        else if (_loadingEntries)
                        {
                            <MudProgressLinear Color="Color.Primary" Indeterminate="true" />
                        }
                        else
                        {
                            <select @bind="_existingEntryId" @bind:after="MarkDirty" style="width:100%;">
                                <option value="0">— Välj verifikation —</option>
                                @foreach (var e in _linkableEntries)
                                {
                                    <option value="@e.Id">#@e.EntryNumber @e.Date.ToString("yyyy-MM-dd") — @e.Description</option>
                                }
                            </select>
                        }
                    }
```

with:

```html
                    else
                    {
                        <MudAutocomplete T="JournalEntry"
                                         Value="_existingEntry"
                                         ValueChanged="OnExistingEntrySelected"
                                         SearchFunc="SearchExistingEntriesAsync"
                                         ToStringFunc="@(e => e is null ? "" : $"#{e.EntryNumber} {e.Date:yyyy-MM-dd} — {e.Description}")"
                                         MinCharacters="0"
                                         Placeholder="Sök verifikation…"
                                         Clearable="true"
                                         FullWidth="true" />
                    }
```

- [ ] **Step 5: Fix `JournalPageTests.cs` for the new signature**

In `tests/KoalaBooks.ComponentTests/JournalPageTests.cs`, add `using KoalaBooks.Domain.Enums;` and `using KoalaBooks.Domain.Interfaces;` to the usings. Replace the setup line:

```csharp
        _journalEntryService.GetByFiscalYearAsync(Arg.Any<int>()).Returns([]);
```

with:

```csharp
        _journalEntryService.GetByFiscalYearAsync(
                Arg.Any<int>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<string?>(),
                Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new PagedResult<JournalEntry> { Items = [], Page = 1, PageSize = 50, TotalCount = 0 });
```

Replace each of the three `Received(1)` assertions, e.g.:

```csharp
        await _journalEntryService.Received(1).GetByFiscalYearAsync(ClosedFy2025.Id);
```

with:

```csharp
        await _journalEntryService.Received(1).GetByFiscalYearAsync(
            ClosedFy2025.Id, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<string?>(),
            Arg.Any<JournalEntrySortBy>(), Arg.Any<int>(), Arg.Any<int>());
```

(apply the same transform to the `OpenFy2026.Id` assertion in `FallsBackToDefault_WhenNoSharedSelection` and the second `ClosedFy2025.Id` assertion in `ChangingFiscalYear_WritesBackToSharedSelection`). NSubstitute's `Received(1)` matches all parameters including defaulted ones, so leaving them unspecified would assert the call had `page=0, pageSize=0` — `Arg.Any<int>()` for those two parameters is required, not optional.

- [ ] **Step 6: Run the component tests**

Run: `dotnet test tests/KoalaBooks.ComponentTests/KoalaBooks.ComponentTests.csproj --filter FullyQualifiedName~JournalPageTests`
Expected: 3 tests pass (`SeedsFromSharedSelection_WhenPresentInFiscalYearList`, `FallsBackToDefault_WhenNoSharedSelection`, `ChangingFiscalYear_WritesBackToSharedSelection`).

Also run the full component test project to catch any other regression from the `ClassifyDocumentDialog.razor` change:
Run: `dotnet test tests/KoalaBooks.ComponentTests/KoalaBooks.ComponentTests.csproj`
Expected: all tests pass, including `PreviewDocumentDialogTests`.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor tests/KoalaBooks.ComponentTests/JournalPageTests.cs
git commit -m "Give Journal.razor real paged/sorted/filtered fetching and search-as-you-type the linkable-entry picker"
```

---

### Task 4: Client (WASM) — `JournalEntryApiService` direct 1:1 mapping to the paged endpoint

**Files:**
- Modify: `src/KoalaBooks.Client/Services/JournalEntryApiService.cs`

**Interfaces:**
- Consumes: `IJournalEntryService.GetByFiscalYearAsync` (Task 1), `PagedResult<T>` (Task 1).
- Produces: no new public surface — implements the interface Task 1 declared, same as Task 2 did for the EF-backed service.

- [ ] **Step 1: Replace `GetByFiscalYearAsync` and its query-building helper**

Replace:

```csharp
    public async Task<List<JournalEntry>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null)
    {
        var query = BuildDateRangeQuery(from, to);
        var page = await http.GetFromJsonAsync<PagedResult<JournalEntryResponse>>(
            $"api/v1/fiscal-years/{fiscalYearId}/journal-entries?pageSize=200{query}", ApiJson.Options)
            .ConfigureAwait(false);
        return page?.Items.Select(ToEntity).ToList() ?? [];
    }
```

with:

```csharp
    public async Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        string? search = null,
        JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
        int page = 1, int pageSize = 50)
    {
        var query = BuildQuery(from, to, search, sortBy, page, pageSize);
        var response = await http.GetFromJsonAsync<PagedResultResponse<JournalEntryResponse>>(
            $"api/v1/fiscal-years/{fiscalYearId}/journal-entries?{query}", ApiJson.Options)
            .ConfigureAwait(false);
        return new PagedResult<JournalEntry>
        {
            Items = response?.Items.Select(ToEntity).ToList() ?? [],
            Page = response?.Page ?? page,
            PageSize = response?.PageSize ?? pageSize,
            TotalCount = response?.TotalCount ?? 0
        };
    }
```

Replace `BuildDateRangeQuery`:

```csharp
    private static string BuildDateRangeQuery(DateOnly? from, DateOnly? to)
    {
        var parts = new List<string>();
        if (from is not null) parts.Add($"from={from:yyyy-MM-dd}");
        if (to is not null) parts.Add($"to={to:yyyy-MM-dd}");
        return parts.Count == 0 ? "" : "&" + string.Join("&", parts);
    }
```

with:

```csharp
    private static string BuildQuery(DateOnly? from, DateOnly? to, string? search, JournalEntrySortBy sortBy, int page, int pageSize)
    {
        var parts = new List<string> { $"page={page}", $"pageSize={pageSize}", $"sortBy={sortBy}" };
        if (from is not null) parts.Add($"from={from:yyyy-MM-dd}");
        if (to is not null) parts.Add($"to={to:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(search)) parts.Add($"search={Uri.EscapeDataString(search)}");
        return string.Join("&", parts);
    }
```

- [ ] **Step 2: Rename the private HTTP-shape record so it doesn't collide with `KoalaBooks.Domain.Interfaces.PagedResult<T>`**

`IJournalEntryService` now returns `KoalaBooks.Domain.Interfaces.PagedResult<JournalEntry>`, and this file already has `using KoalaBooks.Domain.Interfaces;` at the top — so the existing private record with the same simple name will collide. Replace:

```csharp
    private record PagedResult<T>(List<T> Items, int Page, int PageSize, int TotalCount);
```

with:

```csharp
    private record PagedResultResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount);
```

This record exists purely to deserialize the HTTP JSON body (its shape is unchanged); it's a different type from `KoalaBooks.Domain.Interfaces.PagedResult<T>`, which is now the method's actual return type.

- [ ] **Step 3: Verify `KoalaBooks.Client` builds** (via its dependent, since `Client` has no standalone test project — `Components`, which it references, must also be in its Task-3 state)

Run: `dotnet build src/KoalaBooks.Client/KoalaBooks.Client.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Client/Services/JournalEntryApiService.cs
git commit -m "Make JournalEntryApiService a direct 1:1 mapping onto the paged endpoint, deleting the pageSize=200 stand-in-for-everything hack"
```

---

### Task 5: Web — controller pagination params, and the full integration test suite

**Files:**
- Modify: `src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs`
- Modify: `tests/KoalaBooks.Tests/Api/ApiTests.cs` (add new tests; the two existing pagination tests are left as-is since the response envelope is unchanged)
- Create: `tests/KoalaBooks.Tests/JournalEntryPaginationServiceTests.cs`

**Interfaces:**
- Consumes: `IJournalEntryService.GetByFiscalYearAsync` (Task 1/2), `PagedResult<T>` (Task 1).
- Produces: `GET /api/v1/fiscal-years/{fiscalYearId}/journal-entries?from=&to=&search=&sortBy=entryNumber|date&page=&pageSize=` — the full solution (Domain→Application→Components→Client→Web) is buildable and testable after this task.

- [ ] **Step 1: Add `search`/`sortBy` query params to the controller and push them through**

In `src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs`, add `using KoalaBooks.Domain.Enums;` to the usings. Replace:

```csharp
    [HttpGet("fiscal-years/{fiscalYearId:int}/journal-entries")]
    [ProducesResponseType<PagedResult<JournalEntryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _journalEntryService.GetByFiscalYearAsync(fiscalYearId, from, to);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapEntry).ToList();

        return Ok(new PagedResult<JournalEntryResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }
```

with:

```csharp
    [HttpGet("fiscal-years/{fiscalYearId:int}/journal-entries")]
    [ProducesResponseType<PagedResult<JournalEntryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? search,
        [FromQuery] JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var result = await _journalEntryService.GetByFiscalYearAsync(fiscalYearId, from, to, search, sortBy, page, pageSize);

        return Ok(new PagedResult<JournalEntryResponse>
        {
            Items = result.Items.Select(MapEntry).ToList(),
            Page = result.Page,
            PageSize = result.PageSize,
            TotalCount = result.TotalCount
        });
    }
```

An unrecognized `sortBy` value (e.g. `?sortBy=bogus`) never reaches this method body — ASP.NET Core's enum model binder adds a `ModelState` error on parse failure, and `[ApiController]` automatically returns `400` for invalid `ModelState` before the action runs. No extra code is needed for that behavior; Step 4 below verifies it.

- [ ] **Step 2: Verify the full solution builds**

Run: `dotnet build`
Expected: `Build succeeded.` — this is the first point where the whole solution (including `KoalaBooks.Web`, and therefore `tests/KoalaBooks.Tests`) compiles again.

- [ ] **Step 3: Run the two existing pagination tests to confirm the response envelope is unchanged**

Run: `dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj --filter "FullyQualifiedName~JournalEntries_List_ReturnsPaginatedResult|FullyQualifiedName~JournalEntries_List_UnknownFiscalYear_Returns404"`
Expected: both pass unchanged — they assert on `items`/`totalCount`/`page` which are still present with the same names.

- [ ] **Step 4: Add a helper and the new integration tests to `tests/KoalaBooks.Tests/Api/ApiTests.cs`**

Add this private helper near the other helpers (after `SeedSecondTenantAsync`):

```csharp
    private async Task<(int CashId, int RevenueId)> GetAccountIdsAsync(HttpClient client)
    {
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();
        return (cashId, revenueId);
    }

    private async Task<(int Id, int EntryNumber)> CreateEntryAsync(HttpClient client, int cashId, int revenueId, string date, string description)
    {
        var body = new
        {
            date,
            description,
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 100m }
            }
        };
        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetInt32(), json.GetProperty("entryNumber").GetInt32());
    }
```

Add these test methods next to the existing `JournalEntries_List_*` tests:

```csharp
    [Fact]
    public async Task JournalEntries_List_SortByDate_OrdersByDateThenEntryNumber()
    {
        var client = await AuthenticatedClientAsync();
        var (cashId, revenueId) = await GetAccountIdsAsync(client);

        await CreateEntryAsync(client, cashId, revenueId, "2025-03-10", "March entry");
        await CreateEntryAsync(client, cashId, revenueId, "2025-01-05", "January entry");
        await CreateEntryAsync(client, cashId, revenueId, "2025-02-20", "February entry");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?sortBy=date&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var dates = json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("date").GetString()).ToList();
        Assert.Equal(["2025-01-05", "2025-02-20", "2025-03-10"], dates);
    }

    [Fact]
    public async Task JournalEntries_List_UnknownSortBy_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?sortBy=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_List_DateRangeFilter_ReturnsCorrectSliceWithNoDuplicatesAcrossPages()
    {
        var client = await AuthenticatedClientAsync();
        var (cashId, revenueId) = await GetAccountIdsAsync(client);

        for (var i = 1; i <= 5; i++)
            await CreateEntryAsync(client, cashId, revenueId, $"2025-02-{i:D2}", $"February entry {i}");
        await CreateEntryAsync(client, cashId, revenueId, "2025-03-01", "Outside range");

        var page1 = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?from=2025-02-01&to=2025-02-28&page=1&pageSize=2");
        var page2 = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?from=2025-02-01&to=2025-02-28&page=2&pageSize=2");
        var page3 = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?from=2025-02-01&to=2025-02-28&page=3&pageSize=2");

        var json1 = await page1.Content.ReadFromJsonAsync<JsonElement>();
        var json2 = await page2.Content.ReadFromJsonAsync<JsonElement>();
        var json3 = await page3.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(5, json1.GetProperty("totalCount").GetInt32());
        var ids1 = json1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        var ids2 = json2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        var ids3 = json3.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();

        Assert.Equal(2, ids1.Count);
        Assert.Equal(2, ids2.Count);
        Assert.Single(ids3);
        var allIds = ids1.Concat(ids2).Concat(ids3).ToList();
        Assert.Equal(5, allIds.Distinct().Count());
    }

    [Fact]
    public async Task JournalEntries_List_SearchByExactEntryNumber_ReturnsThatEntry()
    {
        var client = await AuthenticatedClientAsync();
        var (cashId, revenueId) = await GetAccountIdsAsync(client);

        await CreateEntryAsync(client, cashId, revenueId, "2025-01-01", "Alpha");
        var (_, secondEntryNumber) = await CreateEntryAsync(client, cashId, revenueId, "2025-01-02", "Beta");
        await CreateEntryAsync(client, cashId, revenueId, "2025-01-03", "Gamma");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?search={secondEntryNumber}");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, json.GetProperty("totalCount").GetInt32());
        Assert.Equal("Beta", json.GetProperty("items").EnumerateArray().First().GetProperty("description").GetString());
    }

    [Fact]
    public async Task JournalEntries_List_SearchByDescriptionSubstring_ReturnsMatchingEntriesCaseInsensitive()
    {
        var client = await AuthenticatedClientAsync();
        var (cashId, revenueId) = await GetAccountIdsAsync(client);

        await CreateEntryAsync(client, cashId, revenueId, "2025-01-01", "Office rent payment");
        await CreateEntryAsync(client, cashId, revenueId, "2025-01-02", "Client invoice");
        await CreateEntryAsync(client, cashId, revenueId, "2025-01-03", "OFFICE supplies");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?search=office");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, json.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task JournalEntries_List_SearchNoMatch_ReturnsEmptyPageWithZeroTotalCount()
    {
        var client = await AuthenticatedClientAsync();
        var (cashId, revenueId) = await GetAccountIdsAsync(client);
        await CreateEntryAsync(client, cashId, revenueId, "2025-01-01", "Alpha");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?search=zzz-nomatch");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("totalCount").GetInt32());
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }
```

- [ ] **Step 5: Add the service-level "never over-fetches" proxy test**

Create `tests/KoalaBooks.Tests/JournalEntryPaginationServiceTests.cs`, matching `JournalEntryDbGuardTests.cs`'s style (direct `TestFixture`, no HTTP layer):

```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class JournalEntryPaginationServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public JournalEntryPaginationServiceTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task GetByFiscalYearAsync_PageSizeSmallerThanTotal_NeverReturnsMoreThanPageSize()
    {
        for (var i = 0; i < 7; i++)
        {
            var (_, error) = await _f.JournalEntryService.CreateAsync(
                _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m, description: $"Entry {i}"));
            Assert.Null(error);
        }

        var result = await _f.JournalEntryService.GetByFiscalYearAsync(_fy.Id, page: 1, pageSize: 3);

        Assert.Equal(7, result.TotalCount);
        Assert.Equal(3, result.Items.Count);
    }
}
```

- [ ] **Step 6: Run the full `KoalaBooks.Tests` project**

Run: `dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj`
Expected: all tests pass, including the 2 pre-existing pagination tests, the 6 new `JournalEntries_List_*` tests, `JournalEntryPaginationServiceTests`, and the fixed `TenantIsolationTests.GetJournalEntriesByFiscalYear_AsOtherTenant_ReturnsEmpty`.

- [ ] **Step 7: Run the whole solution's test suites once more for a final regression check**

Run: `dotnet test`
Expected: `KoalaBooks.Tests` and `KoalaBooks.ComponentTests` both pass in full (no regressions in unrelated suites from the interface/DI-shape change).

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs tests/KoalaBooks.Tests/Api/ApiTests.cs tests/KoalaBooks.Tests/JournalEntryPaginationServiceTests.cs
git commit -m "Wire search/sortBy through JournalEntriesController and add pagination/search/sort integration coverage"
```

---

## Self-Review Notes

- **Spec coverage:** Domain `PagedResult`/`JournalEntrySortBy`/interface (Task 1) → EF push-down query (Task 2) → `Journal.razor` UI + `ClassifyDocumentDialog.razor` autocomplete (Task 3) → WASM client 1:1 mapping, hack deleted (Task 4) → controller query params + testing checklist (Task 5, covers every bullet in the spec's "Testing" section: existing 2 tests untouched, `sortBy=date` ordering, unrecognized `sortBy` → 400, `from`/`to` slice with no dup/missing across pages, exact-entry-number search, description-substring search case-insensitive, no-match search returns empty page not an error, and the `Items.Count <= pageSize` service-level proxy). `SupplierInvoicesController`/`BankTransactionsController` are explicitly out of scope per the spec and untouched by this plan.
- **Placeholder scan:** no TBD/TODO markers; every step has literal code.
- **Type consistency:** `GetByFiscalYearAsync(int, DateOnly?, DateOnly?, string?, JournalEntrySortBy, int, int)` returning `PagedResult<JournalEntry>` is identical across Task 1 (interface), Task 2 (EF impl), Task 3 (callers), Task 4 (WASM impl), Task 5 (controller call). `PagedResult<T>` (`Items`/`Page`/`PageSize`/`TotalCount`) is the same shape in `KoalaBooks.Domain.Interfaces` (Task 1) and the pre-existing `KoalaBooks.Web.Models.Api.PagedResult<T>` (untouched, already matched the wire format) — Task 4 renames the WASM client's colliding private record to `PagedResultResponse<T>` to avoid ambiguity with the newly-Domain-scoped name.
