# Journal / Review Split (#170) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the Journal page into a posted-only ledger (`/journal`) and a new draft-review page (`/review`), per the design in `docs/superpowers/specs/2026-07-01-journal-review-split-design.md`.

**Architecture:** Extract the create/edit form markup and validation currently inline in `Journal.razor` into a shared `JournalEntryForm` component, so both the Journal create flow and the new Review edit flow reuse it. Add a new `Review.razor` page backed by a self-contained `JournalReviewSection` component that owns the accept/edit/decline actions for draft entries. Journal is then trimmed to posted-only entries with a collapsed row-action menu.

**Tech Stack:** Blazor Server (.NET 10), MudBlazor 9.6.0, EF Core / Npgsql, xUnit + Testcontainers.PostgreSql for service-layer tests (no component test framework — UI changes are verified by `dotnet build` + manual browser check).

## Global Constraints

- All UI text is Swedish, matching existing terminology (e.g. "Bokför", "Utkast", "Verifikation", "Räkenskapsår").
- Reuse existing CSS classes (`.card`, `.btn btn-sm btn-success`, `.form-group`, `.toolbar`, `.balance-ok`/`.balance-err`) — do not add new stylesheets or inline style systems.
- No bUnit or other Blazor component test framework exists in `tests/KoalaBooks.Tests` — razor/markup changes are verified via `dotnet build` and manual browser testing, not automated component tests.
- MudBlazor 9.6.0 is already referenced (`KoalaBooks.Components.csproj`) and globally imported via `_Imports.razor` — no new package references needed.
- Reuse existing `JournalEntryService` methods unchanged: `CreateAsync`, `PostAsync`, `UpdateAsync`, `DeleteDraftAsync`, `GetByFiscalYearAsync`. The only new service method is `CountDraftsAsync`.
- Out of scope — do not implement: customer-invoice-from-entry, "convert draft → draft invoice", or "Korrigera" (reverse + recreate in one action). These are follow-up issues per the design doc.
- `dotnet test` requires Docker (Testcontainers.PostgreSql spins up a real Postgres instance per test class via `TestFixture`).

---

## Task 1: `JournalEntryService.CountDraftsAsync`

**Files:**
- Modify: `src/KoalaBooks.Application/Services/JournalEntryService.cs:17-29`
- Test: `tests/KoalaBooks.Tests/CountDraftsAsyncTests.cs` (new)

**Interfaces:**
- Produces: `JournalEntryService.CountDraftsAsync(int fiscalYearId) -> Task<int>` — used by Task 5 (nav badge in `MainLayout.razor`).

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/CountDraftsAsyncTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class CountDraftsAsyncTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public CountDraftsAsyncTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task CountDrafts_NoEntries_ReturnsZero()
    {
        var count = await _f.JournalEntryService.CountDraftsAsync(_fy.Id);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CountDrafts_OnlyCountsUnpostedEntries()
    {
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 200m));
        await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 300m);

        var count = await _f.JournalEntryService.CountDraftsAsync(_fy.Id);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountDrafts_ScopedToFiscalYear()
    {
        var otherFy = _f.CreateFiscalYear("2027", new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31));
        var (otherCash, _, _, otherRevenue, _) = _f.CreateStandardAccounts(otherFy.Id);
        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(otherFy.Id, otherCash.Id, otherRevenue.Id, 500m));

        await _f.JournalEntryService.CreateAsync(_f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m));

        var count = await _f.JournalEntryService.CountDraftsAsync(_fy.Id);

        Assert.Equal(1, count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CountDraftsAsyncTests"`
Expected: FAIL to compile — `'JournalEntryService' does not contain a definition for 'CountDraftsAsync'`

- [ ] **Step 3: Implement `CountDraftsAsync`**

In `src/KoalaBooks.Application/Services/JournalEntryService.cs`, find:

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

        return await query.OrderBy(j => j.EntryNumber).ToListAsync();
    }
```

Replace with (adds the new method directly after it):

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

        return await query.OrderBy(j => j.EntryNumber).ToListAsync();
    }

    public Task<int> CountDraftsAsync(int fiscalYearId) =>
        _db.JournalEntries.CountAsync(j => j.FiscalYearId == fiscalYearId && !j.IsPosted);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CountDraftsAsyncTests"`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/JournalEntryService.cs tests/KoalaBooks.Tests/CountDraftsAsyncTests.cs
git commit -m "feat: add JournalEntryService.CountDraftsAsync"
```

---

## Task 2: Extract `JournalEntryForm` shared component

**Goal of this task:** pure refactor — move the date/description/lines-grid markup and its supporting logic (currently inline in `Journal.razor`) into a new reusable component. Journal's behavior must be byte-for-byte identical after this task (same single "Spara" button, same edit-mode support, same keyboard/focus behavior) — only the implementation moves.

**Files:**
- Create: `src/KoalaBooks.Components/Shared/JournalEntryForm.razor`
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`

**Interfaces:**
- Produces: `JournalEntryForm.LineModel` (public nested class: `AccountId`, `DebitAmount`, `DecimalAmount`... — see exact fields below) and component parameters `Accounts`, `Date`/`DateChanged`, `Description`/`DescriptionChanged`, `Lines`, `IsBalancedChanged`. Task 3 (Review) and Task 4 (Journal cleanup) both consume this component and `JournalEntryForm.LineModel`.

- [ ] **Step 1: Create the component**

Create `src/KoalaBooks.Components/Shared/JournalEntryForm.razor`:

```razor
@using KoalaBooks.Domain.Entities
@inject IJSRuntime JS

<div style="display:grid; grid-template-columns:150px 1fr; gap:0.5rem; margin-bottom:1rem;">
    <div class="form-group">
        <label>Datum</label>
        <DateInput Value="Date" ValueChanged="SetDate" />
    </div>
    <div class="form-group">
        <label>Beskrivning</label>
        <input type="text" @bind:get="Description" @bind:set="SetDescription" placeholder="Beskrivning av transaktion" />
    </div>
</div>

<table style="overflow:visible;">
    <thead>
        <tr>
            <th>Konto</th>
            <th style="width:160px;">Debet</th>
            <th style="width:160px;">Kredit</th>
            <th style="width:60px;"></th>
        </tr>
    </thead>
    <tbody>
        @for (int i = 0; i < Lines.Count; i++)
        {
            var idx = i;
            <tr>
                <td>
                    <AccountSearchDropdown Accounts="Accounts"
                                           @bind-SelectedAccountId="Lines[idx].AccountId"
                                           InputId="@($"line-account-{idx}")"
                                           OnAfterKeyboardSelect="() => FocusDebitAsync(idx)" />
                </td>
                <td><input id="@($"line-debit-{idx}")" type="number" step="0.01" min="0" @bind="Lines[idx].DebitAmount" @bind:after="NotifyBalance" /></td>
                <td><input type="number" step="0.01" min="0" @bind="Lines[idx].CreditAmount" @bind:after="NotifyBalance"
                           @onkeydown="e => HandleCreditKeyDown(e, idx)" /></td>
                <td>
                    @if (Lines.Count > 2)
                    {
                        <button class="btn btn-sm btn-danger" @onclick="() => RemoveLine(idx)">✕</button>
                    }
                </td>
            </tr>
        }
    </tbody>
</table>

<div class="toolbar" style="margin-top:0.5rem;">
    <button class="btn btn-sm btn-secondary" @onclick="AddLine">+ Lägg till rad</button>
    <span>
        Debet: <strong>@TotalDebit.ToString("N2")</strong> |
        Kredit: <strong>@TotalCredit.ToString("N2")</strong> |
        Saldo: <span class="@(IsBalanced ? "balance-ok" : "balance-err")">@((TotalDebit - TotalCredit).ToString("N2"))</span>
    </span>
</div>

@code {
    public class LineModel
    {
        public int AccountId { get; set; }
        public decimal DebitAmount { get; set; }
        public decimal CreditAmount { get; set; }
    }

    [Parameter, EditorRequired] public List<Account> Accounts { get; set; } = [];
    [Parameter] public DateTime Date { get; set; } = DateTime.Today;
    [Parameter] public EventCallback<DateTime> DateChanged { get; set; }
    [Parameter] public string Description { get; set; } = "";
    [Parameter] public EventCallback<string> DescriptionChanged { get; set; }
    [Parameter, EditorRequired] public List<LineModel> Lines { get; set; } = [];
    [Parameter] public EventCallback<bool> IsBalancedChanged { get; set; }

    private string? _pendingFocusId;

    private decimal TotalDebit => Lines.Sum(l => l.DebitAmount);
    private decimal TotalCredit => Lines.Sum(l => l.CreditAmount);
    private bool IsBalanced => TotalDebit > 0 && TotalDebit == TotalCredit;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
            await IsBalancedChanged.InvokeAsync(IsBalanced);

        if (_pendingFocusId is not null)
        {
            var id = _pendingFocusId;
            _pendingFocusId = null;
            await JS.InvokeVoidAsync("koala.focusId", id);
        }
    }

    private async Task SetDate(DateTime value)
    {
        Date = value;
        await DateChanged.InvokeAsync(value);
    }

    private async Task SetDescription(string value)
    {
        Description = value;
        await DescriptionChanged.InvokeAsync(value);
    }

    private void AddLine()
    {
        Lines.Add(new());
        NotifyBalance();
    }

    private void RemoveLine(int idx)
    {
        Lines.RemoveAt(idx);
        NotifyBalance();
    }

    private void HandleCreditKeyDown(KeyboardEventArgs e, int idx)
    {
        if (e.Key == "Tab" && !e.ShiftKey && idx == Lines.Count - 1)
        {
            Lines.Add(new());
            _pendingFocusId = $"line-account-{Lines.Count - 1}";
            NotifyBalance();
        }
    }

    private async Task FocusDebitAsync(int idx) =>
        await JS.InvokeVoidAsync("koala.focusId", $"line-debit-{idx}");

    private void NotifyBalance() => IsBalancedChanged.InvokeAsync(IsBalanced);
}
```

- [ ] **Step 2: Wire `Journal.razor` to use it — remove the JS inject**

In `src/KoalaBooks.Components/Pages/Journal.razor`, find:

```razor
@using Microsoft.AspNetCore.Components.Forms
@inject IJSRuntime JS

<PageTitle>Verifikationer — KoalaBooks</PageTitle>
```

Replace with:

```razor
@using Microsoft.AspNetCore.Components.Forms

<PageTitle>Verifikationer — KoalaBooks</PageTitle>
```

- [ ] **Step 3: Replace the inline form markup with the component**

Find (the entire `_showForm` card block):

```razor
@if (_showForm)
{
    <div class="card">
        <h3>@(_isEditing ? "Redigera" : "Ny") verifikation</h3>
        <div style="display:grid; grid-template-columns:150px 1fr; gap:0.5rem; margin-bottom:1rem;">
            <div class="form-group">
                <label>Datum</label>
                <DateInput @bind-Value="_formDate" />
            </div>
            <div class="form-group">
                <label>Beskrivning</label>
                <input type="text" @bind="_formDescription" placeholder="Beskrivning av transaktion" />
            </div>
        </div>

        <table style="overflow:visible;">
            <thead>
                <tr>
                    <th>Konto</th>
                    <th style="width:160px;">Debet</th>
                    <th style="width:160px;">Kredit</th>
                    <th style="width:60px;"></th>
                </tr>
            </thead>
            <tbody>
                @for (int i = 0; i < _formLines.Count; i++)
                {
                    var idx = i;
                    <tr>
                        <td>
                            <AccountSearchDropdown Accounts="_accounts"
                                                   @bind-SelectedAccountId="_formLines[idx].AccountId"
                                                   InputId="@($"line-account-{idx}")"
                                                   OnAfterKeyboardSelect="() => FocusDebitAsync(idx)" />
                        </td>
                        <td><input id="@($"line-debit-{idx}")" type="number" step="0.01" min="0" @bind="_formLines[idx].DebitAmount" /></td>
                        <td><input type="number" step="0.01" min="0" @bind="_formLines[idx].CreditAmount"
                                   @onkeydown="e => HandleCreditKeyDown(e, idx)" /></td>
                        <td>
                            @if (_formLines.Count > 2)
                            {
                                <button class="btn btn-sm btn-danger" @onclick="() => _formLines.RemoveAt(idx)">✕</button>
                            }
                        </td>
                    </tr>
                }
            </tbody>
        </table>

        <div class="toolbar" style="margin-top:0.5rem;">
            <button class="btn btn-sm btn-secondary" @onclick="AddLine">+ Lägg till rad</button>
            <span>
                Debet: <strong>@TotalDebit.ToString("N2")</strong> |
                Kredit: <strong>@TotalCredit.ToString("N2")</strong> |
                Saldo: <span class="@(IsBalanced ? "balance-ok" : "balance-err")">@((TotalDebit - TotalCredit).ToString("N2"))</span>
            </span>
        </div>

        <div style="margin-top:1rem; display:flex; gap:0.5rem;">
            <button class="btn btn-success" @onclick="SaveEntry" disabled="@(!IsBalanced)">💾 Spara</button>
            <button class="btn btn-secondary" @onclick="CancelForm">Avbryt</button>
        </div>
    </div>
}
```

Replace with:

```razor
@if (_showForm)
{
    <div class="card">
        <h3>@(_isEditing ? "Redigera" : "Ny") verifikation</h3>

        <JournalEntryForm Accounts="_accounts"
                          Date="_formDate" DateChanged="d => _formDate = d"
                          Description="_formDescription" DescriptionChanged="d => _formDescription = d"
                          Lines="_formLines"
                          IsBalancedChanged="b => _isBalanced = b" />

        <div style="margin-top:1rem; display:flex; gap:0.5rem;">
            <button class="btn btn-success" @onclick="SaveEntry" disabled="@(!_isBalanced)">💾 Spara</button>
            <button class="btn btn-secondary" @onclick="CancelForm">Avbryt</button>
        </div>
    </div>
}
```

- [ ] **Step 4: Update the `@code` block — field declarations**

Find:

```razor
    private DateTime _formDate = DateTime.Today;
    private string _formDescription = "";
    private List<LineModel> _formLines = [];
    private int? _reversingEntryId;
```

Replace with:

```razor
    private DateTime _formDate = DateTime.Today;
    private string _formDescription = "";
    private List<JournalEntryForm.LineModel> _formLines = [];
    private bool _isBalanced;
    private int? _reversingEntryId;
```

- [ ] **Step 5: Remove the now-moved focus/balance state and `OnAfterRenderAsync`**

Find:

```razor
    private List<string> _knownSuppliers = [];

    private string? _pendingFocusId;

    private decimal TotalDebit => _formLines.Sum(l => l.DebitAmount);
    private decimal TotalCredit => _formLines.Sum(l => l.CreditAmount);
    private bool IsBalanced => TotalDebit > 0 && TotalDebit == TotalCredit;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_pendingFocusId is not null)
        {
            var id = _pendingFocusId;
            _pendingFocusId = null;
            await JS.InvokeVoidAsync("koala.focusId", id);
        }
    }

    protected override async Task OnInitializedAsync()
```

Replace with:

```razor
    private List<string> _knownSuppliers = [];

    protected override async Task OnInitializedAsync()
```

- [ ] **Step 6: Update `EditEntry` and remove the now-moved line-editing methods**

Find:

```razor
        _formLines = entry.Lines.Select(l => new LineModel
        {
            AccountId = l.AccountId,
            DebitAmount = l.DebitAmount,
            CreditAmount = l.CreditAmount
        }).ToList();
        _showForm = true;
    }

    private void AddLine() => _formLines.Add(new());

    private void HandleCreditKeyDown(KeyboardEventArgs e, int idx)
    {
        if (e.Key == "Tab" && !e.ShiftKey && idx == _formLines.Count - 1)
        {
            _formLines.Add(new());
            _pendingFocusId = $"line-account-{_formLines.Count - 1}";
        }
    }

    private async Task FocusDebitAsync(int idx) =>
        await JS.InvokeVoidAsync("koala.focusId", $"line-debit-{idx}");

    private void CancelForm() => _showForm = false;
```

Replace with:

```razor
        _formLines = entry.Lines.Select(l => new JournalEntryForm.LineModel
        {
            AccountId = l.AccountId,
            DebitAmount = l.DebitAmount,
            CreditAmount = l.CreditAmount
        }).ToList();
        _showForm = true;
    }

    private void CancelForm() => _showForm = false;
```

- [ ] **Step 7: Remove the now-moved `LineModel` class**

Find:

```razor
    private class LineModel
    {
        public int AccountId { get; set; }
        public decimal DebitAmount { get; set; }
        public decimal CreditAmount { get; set; }
    }

    private async Task PostEntry(int entryId)
```

Replace with:

```razor
    private async Task PostEntry(int entryId)
```

- [ ] **Step 8: Build and verify no regressions**

Run: `dotnet build`
Expected: Build succeeds with no errors.

Then manually verify (e.g. via `dotnet run --project src/KoalaBooks.Web`): open `/journal`, create a new entry (check Tab-to-add-line and account search still work, Spara stays disabled until balanced), then edit a draft entry — both should behave exactly as before this task.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Components/Shared/JournalEntryForm.razor src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "refactor: extract JournalEntryForm shared component from Journal.razor"
```

---

## Task 3: Review page — `JournalReviewSection` + `Review.razor`

**Files:**
- Create: `src/KoalaBooks.Components/Shared/JournalReviewSection.razor`
- Create: `src/KoalaBooks.Components/Pages/Review.razor`

**Interfaces:**
- Consumes: `JournalEntryForm` and `JournalEntryForm.LineModel` (Task 2); `JournalEntryService.{GetByFiscalYearAsync, PostAsync, UpdateAsync, DeleteDraftAsync}`; `FiscalYearService.GetActiveAsync`; `AccountService.GetAllAsync`.
- Produces: `/review` route. No other task depends on this one directly, but Task 4 relies on Review existing so drafts created via Journal's "Spara som utkast" have somewhere to go.

- [ ] **Step 1: Create `JournalReviewSection.razor`**

Create `src/KoalaBooks.Components/Shared/JournalReviewSection.razor`:

```razor
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Entities
@inject JournalEntryService JournalEntryService
@inject ISnackbar Snackbar

@if (_editingEntry is not null)
{
    <div class="card">
        <h3>Redigera verifikation #@_editingEntry.EntryNumber</h3>

        <JournalEntryForm Accounts="Accounts"
                          Date="_formDate" DateChanged="d => _formDate = d"
                          Description="_formDescription" DescriptionChanged="d => _formDescription = d"
                          Lines="_formLines"
                          IsBalancedChanged="b => _isBalanced = b" />

        <div style="margin-top:1rem; display:flex; gap:0.5rem;">
            <button class="btn btn-success" @onclick="SaveEdit" disabled="@(!_isBalanced)">💾 Spara</button>
            <button class="btn btn-secondary" @onclick="CancelEdit">Avbryt</button>
        </div>
    </div>
}

<table>
    <thead>
        <tr>
            <th style="width:60px;">#</th>
            <th style="width:110px;">Datum</th>
            <th>Beskrivning</th>
            <th style="width:120px;">Debet</th>
            <th style="width:120px;">Kredit</th>
            <th style="width:220px;">Åtgärder</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var entry in Entries)
        {
            <tr>
                <td>@entry.EntryNumber</td>
                <td>@entry.Date</td>
                <td>@entry.Description</td>
                <td>@entry.Lines.Sum(l => l.DebitAmount).ToString("N2")</td>
                <td>@entry.Lines.Sum(l => l.CreditAmount).ToString("N2")</td>
                <td>
                    @if (_decliningEntryId == entry.Id)
                    {
                        <div style="display:flex; gap:0.25rem; align-items:center;">
                            <span>Är du säker?</span>
                            <button class="btn btn-sm btn-danger" @onclick="() => ConfirmDecline(entry.Id)">Ja, avvisa</button>
                            <button class="btn btn-sm btn-secondary" @onclick="CancelDecline">Avbryt</button>
                        </div>
                    }
                    else
                    {
                        <button class="btn btn-sm btn-secondary" @onclick="() => StartEdit(entry)">Redigera</button>
                        <button class="btn btn-sm btn-success" @onclick="() => AcceptEntry(entry.Id)">Acceptera</button>
                        <button class="btn btn-sm btn-danger" @onclick="() => StartDecline(entry.Id)">Avvisa</button>
                    }
                </td>
            </tr>
        }
    </tbody>
</table>

@if (!Entries.Any())
{
    <MudAlert Severity="Severity.Info" Class="mt-4">Inga utkast att granska.</MudAlert>
}

@code {
    [Parameter, EditorRequired] public List<JournalEntry> Entries { get; set; } = [];
    [Parameter, EditorRequired] public List<Account> Accounts { get; set; } = [];
    [Parameter] public EventCallback OnEntriesChanged { get; set; }

    private JournalEntry? _editingEntry;
    private DateTime _formDate;
    private string _formDescription = "";
    private List<JournalEntryForm.LineModel> _formLines = [];
    private bool _isBalanced;
    private int? _decliningEntryId;

    private void StartEdit(JournalEntry entry)
    {
        _editingEntry = entry;
        _formDate = entry.Date.ToDateTime(TimeOnly.MinValue);
        _formDescription = entry.Description;
        _formLines = entry.Lines.Select(l => new JournalEntryForm.LineModel
        {
            AccountId = l.AccountId,
            DebitAmount = l.DebitAmount,
            CreditAmount = l.CreditAmount
        }).ToList();
    }

    private void CancelEdit() => _editingEntry = null;

    private async Task SaveEdit()
    {
        var entry = new JournalEntry
        {
            Id = _editingEntry!.Id,
            Date = DateOnly.FromDateTime(_formDate),
            Description = _formDescription,
            FiscalYearId = _editingEntry.FiscalYearId,
            Lines = _formLines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.DebitAmount,
                CreditAmount = l.CreditAmount
            }).ToList()
        };

        var (_, error) = await JournalEntryService.UpdateAsync(entry);
        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }

        Snackbar.Add("Verifikation uppdaterad.", Severity.Success);
        _editingEntry = null;
        await OnEntriesChanged.InvokeAsync();
    }

    private async Task AcceptEntry(int entryId)
    {
        var error = await JournalEntryService.PostAsync(entryId);
        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }
        Snackbar.Add("Verifikation bokförd.", Severity.Success);
        await OnEntriesChanged.InvokeAsync();
    }

    private void StartDecline(int entryId) => _decliningEntryId = entryId;

    private void CancelDecline() => _decliningEntryId = null;

    private async Task ConfirmDecline(int entryId)
    {
        var error = await JournalEntryService.DeleteDraftAsync(entryId);
        _decliningEntryId = null;
        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }
        Snackbar.Add("Utkastet avvisat.", Severity.Success);
        await OnEntriesChanged.InvokeAsync();
    }
}
```

- [ ] **Step 2: Create `Review.razor`**

Create `src/KoalaBooks.Components/Pages/Review.razor`:

```razor
@page "/review"
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Entities
@inject JournalEntryService JournalEntryService
@inject FiscalYearService FiscalYearService
@inject AccountService AccountService

<PageTitle>Att granska — KoalaBooks</PageTitle>

<h1>🔍 Att granska</h1>

@if (_activeFiscalYear is null && !_isLoading)
{
    <MudAlert Severity="Severity.Info">Inget aktivt räkenskapsår. <a href="/fiscal-years">Skapa ett</a> först.</MudAlert>
    return;
}

@if (_isLoading)
{
    <MudProgressLinear Color="Color.Primary" Indeterminate="true" Class="mb-4" />
}
else
{
    <JournalReviewSection Entries="_drafts" Accounts="_accounts" OnEntriesChanged="ReloadDraftsAsync" />
}

@code {
    private FiscalYear? _activeFiscalYear;
    private List<Account> _accounts = [];
    private List<JournalEntry> _drafts = [];
    private bool _isLoading;

    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _activeFiscalYear = await FiscalYearService.GetActiveAsync();
        if (_activeFiscalYear is null)
        {
            _isLoading = false;
            return;
        }

        _accounts = (await AccountService.GetAllAsync(_activeFiscalYear.Id)).Where(a => a.IsActive).ToList();
        await ReloadDraftsAsync();
        _isLoading = false;
    }

    private async Task ReloadDraftsAsync()
    {
        var all = await JournalEntryService.GetByFiscalYearAsync(_activeFiscalYear!.Id);
        _drafts = all.Where(e => !e.IsPosted).ToList();
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

Manually verify: navigate to `/review` directly (no nav link yet — that's Task 5). With an active fiscal year and at least one draft (create one via `/journal`'s existing "Spara" button while still in edit mode from Task 2), the draft should list correctly; Redigera should open the edit card and save; Acceptera should post it (it will then also still show on `/journal` since Task 4 hasn't filtered that page yet); Avvisa should show the confirm step and delete it.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Shared/JournalReviewSection.razor src/KoalaBooks.Components/Pages/Review.razor
git commit -m "feat: add Review page for draft journal entries"
```

---

## Task 4: Journal — posted-only filter, row-action menu, two-button create

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`

**Interfaces:**
- Consumes: `JournalEntryForm` (Task 2).
- No other task depends on this one.

- [ ] **Step 1: Filter loaded entries to posted-only**

Find (in `OnInitializedAsync`):

```razor
        _accounts = (await AccountService.GetAllAsync(_activeFiscalYear.Id)).Where(a => a.IsActive).ToList();
        _entries = await JournalEntryService.GetByFiscalYearAsync(_activeFiscalYear.Id);
        _linkedJournalEntryIds = await InvoiceService.GetLinkedJournalEntryIdsAsync(_activeFiscalYear.Id);
```

Replace with:

```razor
        _accounts = (await AccountService.GetAllAsync(_activeFiscalYear.Id)).Where(a => a.IsActive).ToList();
        _entries = (await JournalEntryService.GetByFiscalYearAsync(_activeFiscalYear.Id)).Where(e => e.IsPosted).ToList();
        _linkedJournalEntryIds = await InvoiceService.GetLinkedJournalEntryIdsAsync(_activeFiscalYear.Id);
```

Find (`ReloadEntriesAsync`):

```razor
    private async Task ReloadEntriesAsync()
    {
        _entries = await JournalEntryService.GetByFiscalYearAsync(_activeFiscalYear!.Id);
        _attachmentCounts = await DocumentService.GetCountsForJournalEntriesAsync(_entries.Select(e => e.Id));
    }
```

Replace with:

```razor
    private async Task ReloadEntriesAsync()
    {
        _entries = (await JournalEntryService.GetByFiscalYearAsync(_activeFiscalYear!.Id)).Where(e => e.IsPosted).ToList();
        _attachmentCounts = await DocumentService.GetCountsForJournalEntriesAsync(_entries.Select(e => e.Id));
    }
```

Find (inside `ConfirmConvert`):

```razor
            Snackbar.Add($"Faktura för {result!.SupplierName} skapad och kopplad till verifikation.", Severity.Success);
            _convertingEntryId = null;
            _entries = await JournalEntryService.GetByFiscalYearAsync(_activeFiscalYear.Id);
            _linkedJournalEntryIds = await InvoiceService.GetLinkedJournalEntryIdsAsync(_activeFiscalYear.Id);
            _knownSuppliers = await InvoiceService.GetSuppliersAsync(_activeFiscalYear.Id);
```

Replace with:

```razor
            Snackbar.Add($"Faktura för {result!.SupplierName} skapad och kopplad till verifikation.", Severity.Success);
            _convertingEntryId = null;
            _entries = (await JournalEntryService.GetByFiscalYearAsync(_activeFiscalYear.Id)).Where(e => e.IsPosted).ToList();
            _linkedJournalEntryIds = await InvoiceService.GetLinkedJournalEntryIdsAsync(_activeFiscalYear.Id);
            _knownSuppliers = await InvoiceService.GetSuppliersAsync(_activeFiscalYear.Id);
```

- [ ] **Step 2: Remove the Status column header**

Find:

```razor
        <tr>
            <th style="width:60px;">#</th>
            <th style="width:110px;">Datum</th>
            <th>Beskrivning</th>
            <th style="width:120px;">Debet</th>
            <th style="width:120px;">Kredit</th>
            <th style="width:90px;">Status</th>
            <th style="width:40px;" title="Bilagor"></th>
            <th style="width:180px;">Åtgärder</th>
        </tr>
```

Replace with:

```razor
        <tr>
            <th style="width:60px;">#</th>
            <th style="width:110px;">Datum</th>
            <th>Beskrivning</th>
            <th style="width:120px;">Debet</th>
            <th style="width:120px;">Kredit</th>
            <th style="width:40px;" title="Bilagor"></th>
            <th style="width:120px;">Åtgärder</th>
        </tr>
```

- [ ] **Step 3: Collapse row actions into a menu; remove Status cell and dead draft branch**

Find:

```razor
        @foreach (var entry in _entries)
        {
            var canConvert = entry.IsPosted
                && !entry.IsClosingEntry
                && !_linkedJournalEntryIds.Contains(entry.Id)
                && entry.Lines.Any(l => l.CreditAmount > 0 && l.Account?.AccountNumber?.StartsWith("24") == true);

            <tr>
                <td>@entry.EntryNumber</td>
                <td>@entry.Date</td>
                <td>@entry.Description</td>
                <td>@entry.Lines.Sum(l => l.DebitAmount).ToString("N2")</td>
                <td>@entry.Lines.Sum(l => l.CreditAmount).ToString("N2")</td>
                <td>
                    @if (entry.IsPosted)
                    {
                        <span style="color:#16a34a;">✅ Bokförd</span>
                    }
                    else
                    {
                        <span style="color:#f59e0b;">📝 Utkast</span>
                    }
                </td>
                <td style="text-align:center;">
                    @{ var attachCount = _attachmentCounts.GetValueOrDefault(entry.Id); }
                    <button class="btn btn-sm @(_attachmentEntryId == entry.Id ? "btn-primary" : "btn-secondary")"
                            style="padding:0.1rem 0.35rem; font-size:0.8rem;"
                            title="@(attachCount > 0 ? $"{attachCount} bilaga(or)" : "Lägg till bilaga")"
                            @onclick="() => ToggleAttachments(entry)">
                        📎@(attachCount > 0 ? attachCount.ToString() : "")
                    </button>
                </td>
                <td>
                    @if (_reversingEntryId == entry.Id)
                    {
                        <div style="display:flex; gap:0.25rem; align-items:center;">
                            <input type="text" @bind="_reversalReason" placeholder="Anledning" style="width:120px;" />
                            <button class="btn btn-sm btn-danger" @onclick="() => ConfirmReversal(entry.Id)">Bekräfta</button>
                            <button class="btn btn-sm btn-secondary" @onclick="CancelReversal">Avbryt</button>
                        </div>
                    }
                    else if (_deletingEntryId == entry.Id)
                    {
                        <div style="display:flex; gap:0.25rem; align-items:center;">
                            <span>Är du säker?</span>
                            <button class="btn btn-sm btn-danger" @onclick="() => ConfirmDelete(entry.Id)">Ja, radera</button>
                            <button class="btn btn-sm btn-secondary" @onclick="CancelDelete">Avbryt</button>
                        </div>
                    }
                    else if (_convertingEntryId == entry.Id)
                    {
                        <button class="btn btn-sm btn-secondary" @onclick="CancelConvert">Avbryt</button>
                    }
                    else if (entry.IsPosted)
                    {
                        <button class="btn btn-sm btn-warning" @onclick="() => StartReversal(entry.Id)">Återför</button>
                        @if (canConvert)
                        {
                            <button class="btn btn-sm btn-primary" style="margin-left:0.25rem;"
                                    @onclick="() => StartConvert(entry)">Konvertera</button>
                        }
                        @if (_linkedJournalEntryIds.Contains(entry.Id))
                        {
                            <span style="font-size:0.75rem; color:#16a34a; margin-left:0.25rem;">📄 Faktura</span>
                        }
                    }
                    else
                    {
                        <button class="btn btn-sm btn-secondary" @onclick="() => EditEntry(entry)">Redigera</button>
                        <button class="btn btn-sm btn-success" @onclick="() => PostEntry(entry.Id)">Bokför</button>
                        <button class="btn btn-sm btn-danger" @onclick="() => StartDelete(entry.Id)">🗑️ Radera</button>
                    }
                </td>
            </tr>
```

Replace with:

```razor
        @foreach (var entry in _entries)
        {
            var canConvert = !entry.IsClosingEntry
                && !_linkedJournalEntryIds.Contains(entry.Id)
                && entry.Lines.Any(l => l.CreditAmount > 0 && l.Account?.AccountNumber?.StartsWith("24") == true);

            <tr>
                <td>@entry.EntryNumber</td>
                <td>@entry.Date</td>
                <td>@entry.Description</td>
                <td>@entry.Lines.Sum(l => l.DebitAmount).ToString("N2")</td>
                <td>@entry.Lines.Sum(l => l.CreditAmount).ToString("N2")</td>
                <td style="text-align:center;">
                    @{ var attachCount = _attachmentCounts.GetValueOrDefault(entry.Id); }
                    <button class="btn btn-sm @(_attachmentEntryId == entry.Id ? "btn-primary" : "btn-secondary")"
                            style="padding:0.1rem 0.35rem; font-size:0.8rem;"
                            title="@(attachCount > 0 ? $"{attachCount} bilaga(or)" : "Lägg till bilaga")"
                            @onclick="() => ToggleAttachments(entry)">
                        📎@(attachCount > 0 ? attachCount.ToString() : "")
                    </button>
                </td>
                <td>
                    @if (_reversingEntryId == entry.Id)
                    {
                        <div style="display:flex; gap:0.25rem; align-items:center;">
                            <input type="text" @bind="_reversalReason" placeholder="Anledning" style="width:120px;" />
                            <button class="btn btn-sm btn-danger" @onclick="() => ConfirmReversal(entry.Id)">Bekräfta</button>
                            <button class="btn btn-sm btn-secondary" @onclick="CancelReversal">Avbryt</button>
                        </div>
                    }
                    else if (_convertingEntryId == entry.Id)
                    {
                        <button class="btn btn-sm btn-secondary" @onclick="CancelConvert">Avbryt</button>
                    }
                    else
                    {
                        <MudMenu Icon="@Icons.Material.Filled.MoreVert" Size="Size.Small" Dense="true">
                            <MudMenuItem OnClick="() => StartReversal(entry.Id)">Återför</MudMenuItem>
                            @if (canConvert)
                            {
                                <MudMenuItem OnClick="() => StartConvert(entry)">Skapa leverantörsfaktura</MudMenuItem>
                            }
                        </MudMenu>
                        @if (_linkedJournalEntryIds.Contains(entry.Id))
                        {
                            <span style="font-size:0.75rem; color:#16a34a; margin-left:0.25rem;">📄 Faktura</span>
                        }
                    }
                </td>
            </tr>
```

- [ ] **Step 4: Fix `colspan` on the two expandable panel rows and rename the convert panel heading**

Find:

```razor
                    <td colspan="8" style="padding:1rem;">
                        <div style="max-width:640px;">
                            <p style="margin:0 0 0.75rem; font-weight:600;">📎 Bilagor — Verifikation #@entry.EntryNumber</p>
```

Replace with:

```razor
                    <td colspan="7" style="padding:1rem;">
                        <div style="max-width:640px;">
                            <p style="margin:0 0 0.75rem; font-weight:600;">📎 Bilagor — Verifikation #@entry.EntryNumber</p>
```

Find:

```razor
                    <td colspan="8" style="padding:1rem;">
                        <div style="max-width:700px;">
                            <p style="margin:0 0 0.75rem; font-weight:600;">Konvertera till leverantörsfaktura</p>
```

Replace with:

```razor
                    <td colspan="7" style="padding:1rem;">
                        <div style="max-width:700px;">
                            <p style="margin:0 0 0.75rem; font-weight:600;">Skapa leverantörsfaktura</p>
```

- [ ] **Step 5: Simplify the create-form card — drop edit mode, add the two submit buttons**

Find:

```razor
@if (_showForm)
{
    <div class="card">
        <h3>@(_isEditing ? "Redigera" : "Ny") verifikation</h3>

        <JournalEntryForm Accounts="_accounts"
                          Date="_formDate" DateChanged="d => _formDate = d"
                          Description="_formDescription" DescriptionChanged="d => _formDescription = d"
                          Lines="_formLines"
                          IsBalancedChanged="b => _isBalanced = b" />

        <div style="margin-top:1rem; display:flex; gap:0.5rem;">
            <button class="btn btn-success" @onclick="SaveEntry" disabled="@(!_isBalanced)">💾 Spara</button>
            <button class="btn btn-secondary" @onclick="CancelForm">Avbryt</button>
        </div>
    </div>
}
```

Replace with:

```razor
@if (_showForm)
{
    <div class="card">
        <h3>Ny verifikation</h3>

        <JournalEntryForm Accounts="_accounts"
                          Date="_formDate" DateChanged="d => _formDate = d"
                          Description="_formDescription" DescriptionChanged="d => _formDescription = d"
                          Lines="_formLines"
                          IsBalancedChanged="b => _isBalanced = b" />

        <div style="margin-top:1rem; display:flex; gap:0.5rem;">
            <button class="btn btn-success" @onclick="SaveAndPost" disabled="@(!_isBalanced)">💾 Bokför</button>
            <button class="btn btn-secondary" @onclick="SaveAsDraft" disabled="@(!_isBalanced)">Spara som utkast</button>
            <button class="btn btn-secondary" @onclick="CancelForm">Avbryt</button>
        </div>
    </div>
}
```

- [ ] **Step 6: Remove `_isEditing`/`_editingId`/`_deletingEntryId` fields**

Find:

```razor
    private FiscalYear? _activeFiscalYear;
    private List<Account> _accounts = [];
    private List<JournalEntry> _entries = [];
    private bool _showForm;
    private bool _isEditing;
    private bool _isLoading;
    private int? _editingId;
    private DateTime _formDate = DateTime.Today;
    private string _formDescription = "";
    private List<JournalEntryForm.LineModel> _formLines = [];
    private bool _isBalanced;
    private int? _reversingEntryId;
    private string _reversalReason = "";
    private int? _deletingEntryId;
    private HashSet<int> _linkedJournalEntryIds = [];
```

Replace with:

```razor
    private FiscalYear? _activeFiscalYear;
    private List<Account> _accounts = [];
    private List<JournalEntry> _entries = [];
    private bool _showForm;
    private bool _isLoading;
    private DateTime _formDate = DateTime.Today;
    private string _formDescription = "";
    private List<JournalEntryForm.LineModel> _formLines = [];
    private bool _isBalanced;
    private int? _reversingEntryId;
    private string _reversalReason = "";
    private HashSet<int> _linkedJournalEntryIds = [];
```

- [ ] **Step 7: Simplify `NewEntry`**

Find:

```razor
    private void NewEntry()
    {
        _isEditing = false;
        _editingId = null;
        _formDate = DateTime.Today;
        _formDescription = "";
        _formLines = [new(), new()];
        _showForm = true;
    }
```

Replace with:

```razor
    private void NewEntry()
    {
        _formDate = DateTime.Today;
        _formDescription = "";
        _formLines = [new(), new()];
        _showForm = true;
    }
```

- [ ] **Step 8: Remove `EditEntry`**

Find:

```razor
    private void EditEntry(JournalEntry entry)
    {
        _isEditing = true;
        _editingId = entry.Id;
        _formDate = entry.Date.ToDateTime(TimeOnly.MinValue);
        _formDescription = entry.Description;
        _formLines = entry.Lines.Select(l => new JournalEntryForm.LineModel
        {
            AccountId = l.AccountId,
            DebitAmount = l.DebitAmount,
            CreditAmount = l.CreditAmount
        }).ToList();
        _showForm = true;
    }

    private void CancelForm() => _showForm = false;
```

Replace with:

```razor
    private void CancelForm() => _showForm = false;
```

- [ ] **Step 9: Replace `SaveEntry` with the two-button flow**

Find:

```razor
    private async Task SaveEntry()
    {
        var entry = new JournalEntry
        {
            Id = _editingId ?? 0,
            Date = DateOnly.FromDateTime(_formDate),
            Description = _formDescription,
            FiscalYearId = _activeFiscalYear!.Id,
            Lines = _formLines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.DebitAmount,
                CreditAmount = l.CreditAmount
            }).ToList()
        };

        var (result, error) = _isEditing
            ? await JournalEntryService.UpdateAsync(entry)
            : await JournalEntryService.CreateAsync(entry);

        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }

        Snackbar.Add(_isEditing ? "Verifikation uppdaterad." : $"Verifikation #{result!.EntryNumber} skapad.", Severity.Success);
        _showForm = false;
        await ReloadEntriesAsync();
    }
```

Replace with:

```razor
    private async Task SaveAndPost() => await SaveEntryAsync(post: true);

    private async Task SaveAsDraft() => await SaveEntryAsync(post: false);

    private async Task SaveEntryAsync(bool post)
    {
        var entry = new JournalEntry
        {
            Date = DateOnly.FromDateTime(_formDate),
            Description = _formDescription,
            FiscalYearId = _activeFiscalYear!.Id,
            Lines = _formLines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.DebitAmount,
                CreditAmount = l.CreditAmount
            }).ToList()
        };

        var (result, error) = await JournalEntryService.CreateAsync(entry);
        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }

        if (post)
        {
            var postError = await JournalEntryService.PostAsync(result!.Id);
            if (postError is not null)
            {
                Snackbar.Add(postError, Severity.Error);
                return;
            }
        }

        Snackbar.Add(post
            ? $"Verifikation #{result!.EntryNumber} bokförd."
            : $"Verifikation #{result!.EntryNumber} sparad som utkast.",
            Severity.Success);
        _showForm = false;
        await ReloadEntriesAsync();
    }
```

- [ ] **Step 10: Remove `PostEntry`, `StartDelete`, `CancelDelete`, `ConfirmDelete`**

Find:

```razor
    private async Task PostEntry(int entryId)
    {
        var error = await JournalEntryService.PostAsync(entryId);
        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }
        Snackbar.Add("Verifikation bokförd.", Severity.Success);
        await ReloadEntriesAsync();
    }

    private void StartDelete(int entryId) => _deletingEntryId = entryId;

    private void CancelDelete() => _deletingEntryId = null;

    private async Task ConfirmDelete(int entryId)
    {
        var error = await JournalEntryService.DeleteDraftAsync(entryId);
        _deletingEntryId = null;
        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }
        Snackbar.Add("Utkastet raderat.", Severity.Success);
        await ReloadEntriesAsync();
    }

    private void StartReversal(int entryId)
```

Replace with:

```razor
    private void StartReversal(int entryId)
```

- [ ] **Step 11: Build and verify**

Run: `dotnet build`
Expected: Build succeeds with no errors or unused-symbol warnings related to the removed members.

Manually verify on `/journal`:
- Table shows only posted entries, no Status column.
- Creating a new entry: "Bokför" posts it immediately (appears on Journal); "Spara som utkast" creates a draft (does NOT appear on Journal, appears on `/review` instead).
- Row menu (⋮) shows "Återför" always, "Skapa leverantörsfaktura" only when the entry has an unlinked payable-like credit line; clicking either opens the same inline forms as before.
- Attachments button still works.

- [ ] **Step 12: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "feat: filter Journal to posted entries, collapse row actions into menu"
```

---

## Task 5: Nav link + draft-count badge

**Files:**
- Modify: `src/KoalaBooks.Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: `JournalEntryService.CountDraftsAsync` (Task 1).

- [ ] **Step 1: Add the nav link**

Find:

```razor
                <MudNavLink Href="/journal" Icon="@Icons.Material.Outlined.MenuBook">Journal</MudNavLink>
                <MudNavLink Href="/inbox" Icon="@Icons.Material.Outlined.Inbox">Inbox</MudNavLink>
```

Replace with:

```razor
                <MudNavLink Href="/journal" Icon="@Icons.Material.Outlined.MenuBook">Journal</MudNavLink>
                <MudNavLink Href="/review" Icon="@Icons.Material.Outlined.PlaylistAddCheck">
                    Att granska
                    @if (_draftCount > 0)
                    {
                        <span style="display:inline-flex; align-items:center; margin-left:auto; background:var(--mud-palette-error); color:var(--mud-palette-error-text); border-radius:9999px; padding:1px 7px; font-size:0.7rem; font-weight:700; line-height:1.6;">@_draftCount</span>
                    }
                </MudNavLink>
                <MudNavLink Href="/inbox" Icon="@Icons.Material.Outlined.Inbox">Inbox</MudNavLink>
```

- [ ] **Step 2: Add badge state fields**

Find:

```razor
    private int _todoCount;
    private bool _loadingTodoCount;
```

Replace with:

```razor
    private int _todoCount;
    private bool _loadingTodoCount;
    private int _draftCount;
    private bool _loadingDraftCount;
```

- [ ] **Step 3: Load the draft count on init and on navigation**

Find:

```razor
    protected override async Task OnInitializedAsync()
    {
        Navigation.LocationChanged += OnLocationChanged;
        await LoadTodoCountAsync();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_loadingTodoCount) return;
        _ = InvokeAsync(async () =>
        {
            await LoadTodoCountAsync();
            StateHasChanged();
        });
    }
```

Replace with:

```razor
    protected override async Task OnInitializedAsync()
    {
        Navigation.LocationChanged += OnLocationChanged;
        await LoadTodoCountAsync();
        await LoadDraftCountAsync();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (!_loadingTodoCount)
        {
            _ = InvokeAsync(async () =>
            {
                await LoadTodoCountAsync();
                StateHasChanged();
            });
        }
        if (!_loadingDraftCount)
        {
            _ = InvokeAsync(async () =>
            {
                await LoadDraftCountAsync();
                StateHasChanged();
            });
        }
    }
```

- [ ] **Step 4: Add `LoadDraftCountAsync`**

Find:

```razor
    private async Task LoadTodoCountAsync()
    {
        if (_loadingTodoCount) return;
        _loadingTodoCount = true;
        try
        {
            // Use a dedicated scope so this background query doesn't share a DbContext
            // with the page's OnInitializedAsync, which would cause concurrent-operation exceptions.
            await using var scope = ScopeFactory.CreateAsyncScope();
            var fySvc = scope.ServiceProvider.GetRequiredService<FiscalYearService>();
            var bankSvc = scope.ServiceProvider.GetRequiredService<BankImportService>();
            var invSvc = scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>();

            var fy = await fySvc.GetActiveAsync();
            if (fy is null) { _todoCount = 0; return; }

            var unmatched = await bankSvc.CountUnmatchedAsync(fy.Id);
            var unpaid = await invSvc.CountUnpaidAsync(fy.Id);
            _todoCount = unmatched + unpaid;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load to-do count for nav badge");
            _todoCount = 0;
        }
        finally
        {
            _loadingTodoCount = false;
        }
    }

    public void Dispose()
```

Replace with:

```razor
    private async Task LoadTodoCountAsync()
    {
        if (_loadingTodoCount) return;
        _loadingTodoCount = true;
        try
        {
            // Use a dedicated scope so this background query doesn't share a DbContext
            // with the page's OnInitializedAsync, which would cause concurrent-operation exceptions.
            await using var scope = ScopeFactory.CreateAsyncScope();
            var fySvc = scope.ServiceProvider.GetRequiredService<FiscalYearService>();
            var bankSvc = scope.ServiceProvider.GetRequiredService<BankImportService>();
            var invSvc = scope.ServiceProvider.GetRequiredService<SupplierInvoiceService>();

            var fy = await fySvc.GetActiveAsync();
            if (fy is null) { _todoCount = 0; return; }

            var unmatched = await bankSvc.CountUnmatchedAsync(fy.Id);
            var unpaid = await invSvc.CountUnpaidAsync(fy.Id);
            _todoCount = unmatched + unpaid;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load to-do count for nav badge");
            _todoCount = 0;
        }
        finally
        {
            _loadingTodoCount = false;
        }
    }

    private async Task LoadDraftCountAsync()
    {
        if (_loadingDraftCount) return;
        _loadingDraftCount = true;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var fySvc = scope.ServiceProvider.GetRequiredService<FiscalYearService>();
            var journalSvc = scope.ServiceProvider.GetRequiredService<JournalEntryService>();

            var fy = await fySvc.GetActiveAsync();
            if (fy is null) { _draftCount = 0; return; }

            _draftCount = await journalSvc.CountDraftsAsync(fy.Id);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load draft count for nav badge");
            _draftCount = 0;
        }
        finally
        {
            _loadingDraftCount = false;
        }
    }

    public void Dispose()
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

Manually verify: with one or more drafts in the active fiscal year, the "Att granska" nav link shows a red count badge matching the number of drafts; creating/accepting/declining a draft and navigating to any page updates the badge (it refreshes on navigation, same as the existing "Att göra" badge).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Layout/MainLayout.razor
git commit -m "feat: add Att granska nav link with draft-count badge"
```

---

## Task 6: Full manual verification

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass (requires Docker for Testcontainers.PostgreSql).

- [ ] **Step 2: Run the app and walk the golden path**

Run: `dotnet run --project src/KoalaBooks.Web` (or use the project's `run`/`verify` skill if available), then in a browser:

1. Go to `/journal`. Confirm the table shows only posted entries and has no Status column.
2. Click "+ Ny verifikation", fill in a balanced entry, click "Bokför". Confirm it appears on `/journal` immediately.
3. Click "+ Ny verifikation" again, fill in a balanced entry, click "Spara som utkast". Confirm it does NOT appear on `/journal`.
4. Go to `/review` (via the "Att granska" nav link). Confirm the draft from step 3 is listed, and the nav badge count matches.
5. Click "Redigera" on the draft, change the description, click "Spara". Confirm the change is reflected in the list.
6. Click "Acceptera". Confirm the entry disappears from `/review` and now appears on `/journal`, and the nav badge count decreases.
7. Create another draft from `/journal`, go to `/review`, click "Avvisa", confirm via "Ja, avvisa". Confirm the draft is gone from both `/review` and `/journal`.
8. On `/journal`, open the row menu (⋮) for a posted entry with a payable-like credit line. Confirm "Återför" and "Skapa leverantörsfaktura" both appear and work as before (reversal creates a new posted entry; creating the invoice links it and shows the "📄 Faktura" badge, after which "Skapa leverantörsfaktura" no longer appears in the menu for that row).
9. Confirm attachments (📎) still work on `/journal` rows.

- [ ] **Step 3: Report results**

If all checks pass, the feature is complete. If anything fails, fix it in the relevant task's files, re-run `dotnet build`/`dotnet test`, and amend with a new commit (do not force-push or rewrite prior commits).
