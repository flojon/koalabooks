# Storno Preview Popup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Journal page's inline reversal (storno) row — a bare reason textbox plus Bekräfta/Avbryt — with a popup that previews exactly what the reversal will create (entry number, date, mirrored lines) before the user confirms.

**Architecture:** Split `JournalEntryService.CreateReversalAsync`'s validation+build logic out into two private helpers (`LoadAndValidateForReversalAsync`, `BuildReversalAsync`) shared by the existing `CreateReversalAsync` and a new read-only `PreviewReversalAsync`, so the preview can never drift from what actually gets persisted. Add a new `Shared/ReversalPreviewDialog.razor` component, following the codebase's existing plain-`<MudDialog>`-conditionally-rendered convention (see `ClassifyDocumentDialog.razor`), and wire it into `Journal.razor` in place of the inline row.

**Tech Stack:** .NET 10 / EF Core (Npgsql/PostgreSQL), Blazor Server + MudBlazor, xUnit + Testcontainers.PostgreSql (no bUnit — this codebase has no Blazor component tests; verification of the new component is manual/browser-based, matching existing convention).

## Global Constraints

- Target framework is `net10.0` everywhere — do not change `TargetFramework`.
- DB provider is PostgreSQL (Npgsql) in every environment; tests use Testcontainers.PostgreSql via `TestFixture`, which calls `Db.Database.EnsureCreated()` — no migration is needed for this feature (no schema change).
- Multi-tenant query filters exist on `JournalEntry`/`JournalEntryLine` — this plan does not touch tenant scoping.
- No `DialogService`/`MudDialogProvider` is registered in this app. Popups are plain `<MudDialog>` components conditionally rendered by the parent page (see `Shared/ClassifyDocumentDialog.razor`, `Shared/PreviewDocumentDialog.razor`, and their usage in `Pages/Inbox.razor:113-127`). Follow this exact pattern — do not introduce `IDialogService`.
- `CreateReversalAsync(int entryId, string reason)`'s public signature must not change — `JournalEntriesController.Reverse` (`src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs:116-126`) calls it directly and is out of scope for this plan.

---

### Task 1: Split `CreateReversalAsync` and add `PreviewReversalAsync`

**Files:**
- Modify: `src/KoalaBooks.Application/Services/JournalEntryService.cs:163-214`
- Test: `tests/KoalaBooks.Tests/PreviewReversalAsyncTests.cs` (new)

**Interfaces:**
- Consumes: existing `JournalEntry`/`JournalEntryLine`/`FiscalYear` entities, existing `TestFixture` helpers (`CreateFiscalYear`, `CreateStandardAccounts`, `CreateAndPostEntryAsync`, `MakeEntry`, `Db`).
- Produces: `JournalEntryService.PreviewReversalAsync(int entryId, string reason) : Task<(JournalEntry? Preview, string? Error)>` — Task 2/3 call this. `CreateReversalAsync`'s existing signature and behavior are unchanged (verified by existing tests, not just new ones).

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/PreviewReversalAsyncTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class PreviewReversalAsyncTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;
    private readonly Account _revenue;

    public PreviewReversalAsyncTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _revenue, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task PreviewReversalAsync_MatchesWhatCreateReversalAsyncProduces()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);

        var (preview, previewError) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Wrong amount");
        Assert.Null(previewError);
        Assert.NotNull(preview);

        var (created, createError) = await _f.JournalEntryService.CreateReversalAsync(posted.Id, "Wrong amount");
        Assert.Null(createError);
        Assert.NotNull(created);

        Assert.Equal(preview!.EntryNumber, created!.EntryNumber);
        Assert.Equal(preview.Date, created.Date);
        Assert.Equal(preview.Description, created.Description);
        Assert.Equal(
            preview.Lines.Select(l => (l.AccountId, l.DebitAmount, l.CreditAmount)).ToList(),
            created.Lines.Select(l => (l.AccountId, l.DebitAmount, l.CreditAmount)).ToList());
    }

    [Fact]
    public async Task PreviewReversalAsync_DoesNotPersistAnythingOrMutateOriginal()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 250m);

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Just checking");
        Assert.Null(error);
        Assert.NotNull(preview);

        var count = await _f.Db.JournalEntries.CountAsync();
        Assert.Equal(1, count); // only the original — preview created nothing

        var reloadedOriginal = await _f.Db.JournalEntries.FindAsync(posted.Id);
        Assert.Equal(JournalEntryStatus.Posted, reloadedOriginal!.Status); // not flipped to Reversed
    }

    [Fact]
    public async Task PreviewReversalAsync_EntryNotPosted_ReturnsError()
    {
        var draft = _f.MakeEntry(_fy.Id, _cash.Id, _revenue.Id, 100m);
        var (created, _) = await _f.JournalEntryService.CreateAsync(draft);

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(created!.Id, "n/a");

        Assert.Null(preview);
        Assert.NotNull(error);
        Assert.Contains("Can only reverse posted entries", error);
    }

    [Fact]
    public async Task PreviewReversalAsync_AlreadyReversedEntry_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);
        await _f.JournalEntryService.CreateReversalAsync(posted.Id, "First reversal");

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Second attempt");

        Assert.Null(preview);
        Assert.NotNull(error);
        Assert.Contains("already been reversed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewReversalAsync_ClosedFiscalYear_ReturnsError()
    {
        var posted = await _f.CreateAndPostEntryAsync(_fy.Id, _cash.Id, _revenue.Id, 400m);

        _fy.IsClosed = true;
        await _f.Db.SaveChangesAsync();

        var (preview, error) = await _f.JournalEntryService.PreviewReversalAsync(posted.Id, "Correction");

        Assert.Null(preview);
        Assert.NotNull(error);
        Assert.Contains("closed", error, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~PreviewReversalAsyncTests`
Expected: build error (`PreviewReversalAsync` does not exist on `JournalEntryService`) or all 5 tests FAIL.

- [ ] **Step 3: Refactor `CreateReversalAsync` and add `PreviewReversalAsync`**

In `src/KoalaBooks.Application/Services/JournalEntryService.cs`, replace the entire existing `CreateReversalAsync` method (currently lines 163–214) with:

```csharp
    public async Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason)
    {
        var (original, error) = await LoadAndValidateForReversalAsync(entryId);
        if (error is not null)
            return (null, error);

        var reversal = await BuildReversalAsync(original!, reason);

        original!.Status = JournalEntryStatus.Reversed;

        _db.JournalEntries.Add(reversal);
        await _db.SaveChangesAsync();

        await PropagateAffectedAccountsAsync(
            reversal.FiscalYearId, reversal.Lines.Select(l => l.AccountId));
        return (reversal, null);
    }

    public async Task<(JournalEntry? Preview, string? Error)> PreviewReversalAsync(int entryId, string reason)
    {
        var (original, error) = await LoadAndValidateForReversalAsync(entryId);
        if (error is not null)
            return (null, error);

        var preview = await BuildReversalAsync(original!, reason);
        return (preview, null);
    }

    private async Task<(JournalEntry? Original, string? Error)> LoadAndValidateForReversalAsync(int entryId)
    {
        var original = await _db.JournalEntries
            .Include(j => j.Lines)
            .Include(j => j.FiscalYear)
            .FirstOrDefaultAsync(j => j.Id == entryId);

        if (original is null)
            return (null, "Journal entry not found.");
        if (!original.IsPosted)
            return (null, "Can only reverse posted entries.");
        if (original.Status == JournalEntryStatus.Reversed)
            return (null, "Journal entry has already been reversed.");
        if (original.FiscalYear.IsClosed)
            return (null, "Cannot create reversals in a closed fiscal year.");

        return (original, null);
    }

    // Pure construction of the mirrored reversal entry. Must not mutate `original`
    // (in particular, never set original.Status here) — PreviewReversalAsync calls
    // this without ever calling SaveChangesAsync, and mutating a tracked entity would
    // dirty the scoped DbContext even without an explicit save.
    private async Task<JournalEntry> BuildReversalAsync(JournalEntry original, string reason)
    {
        var maxNumber = await _db.JournalEntries
            .Where(j => j.FiscalYearId == original.FiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber) ?? 0;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var reversalDate = today <= original.FiscalYear.EndDate && today >= original.FiscalYear.StartDate
            ? today
            : original.FiscalYear.EndDate;

        return new JournalEntry
        {
            EntryNumber = maxNumber + 1,
            FiscalYearId = original.FiscalYearId,
            Date = reversalDate,
            Description = $"Reversal of #{original.EntryNumber}: {reason}",
            CreatedAt = DateTime.UtcNow,
            IsPosted = true,
            Status = JournalEntryStatus.Correction,
            SourceJournalEntryId = original.Id,
            Lines = original.Lines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.CreditAmount,
                CreditAmount = l.DebitAmount
            }).ToList()
        };
    }
```

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter FullyQualifiedName~PreviewReversalAsyncTests`
Expected: PASS (5/5).

- [ ] **Step 5: Run the full existing reversal-related test suites to confirm no regression**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~ReversalDateClampingTests | FullyQualifiedName~ReversalClosedYearTests | FullyQualifiedName~JournalEntryStatusTests | FullyQualifiedName~AuditTrailTests | FullyQualifiedName~JournalEntryDbGuardTests"`
Expected: PASS, same count as before the change (this refactor is behavior-preserving for `CreateReversalAsync`).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests PASS, no failures introduced elsewhere.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Application/Services/JournalEntryService.cs tests/KoalaBooks.Tests/PreviewReversalAsyncTests.cs
git commit -m "feat: add PreviewReversalAsync sharing CreateReversalAsync's build logic"
```

---

### Task 2: `Shared/JournalLinesTable.razor` and `Shared/ReversalPreviewDialog.razor` components

**Files:**
- Create: `src/KoalaBooks.Components/Shared/JournalLinesTable.razor`
- Create: `src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor`

**Interfaces:**
- Consumes: `JournalEntryService.PreviewReversalAsync(int, string)` and `.CreateReversalAsync(int, string)` (Task 1). `JournalEntry`, `JournalEntryLine`, `Account` entities (existing).
- Produces: `JournalLinesTable` component with parameters `Lines` (`List<JournalEntryLine>`, required) and `AccountsById` (`Dictionary<int, Account>`, required) — a small reusable renderer for a debit/credit line table, used twice by `ReversalPreviewDialog` below (once for the original entry, once for the preview) so the markup isn't duplicated. `ReversalPreviewDialog` component with parameters `Entry` (`JournalEntry`, required), `Accounts` (`List<Account>`, required), `OnReversed` (`EventCallback<JournalEntry>`, required — invoked with the newly created reversal entry after a successful confirm), `OnClose` (`EventCallback`, required). Task 3 renders `ReversalPreviewDialog` and wires these parameters.

- [ ] **Step 1: Create the shared line-table component**

Create `src/KoalaBooks.Components/Shared/JournalLinesTable.razor`:

```razor
@* src/KoalaBooks.Components/Shared/JournalLinesTable.razor *@
@using KoalaBooks.Domain.Entities

<table style="font-size:0.85rem; width:100%; margin-bottom:0.5rem;">
    <thead>
        <tr>
            <th>Konto</th>
            <th style="width:100px; text-align:right;">Debet</th>
            <th style="width:100px; text-align:right;">Kredit</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var line in Lines)
        {
            <tr>
                <td>@AccountDisplay(line.AccountId)</td>
                <td style="text-align:right;">@(line.DebitAmount == 0 ? "" : line.DebitAmount.ToString("N2"))</td>
                <td style="text-align:right;">@(line.CreditAmount == 0 ? "" : line.CreditAmount.ToString("N2"))</td>
            </tr>
        }
    </tbody>
</table>

@code {
    [Parameter, EditorRequired] public List<JournalEntryLine> Lines { get; set; } = [];
    [Parameter, EditorRequired] public Dictionary<int, Account> AccountsById { get; set; } = [];

    private string AccountDisplay(int accountId) =>
        AccountsById.TryGetValue(accountId, out var a) ? $"{a.AccountNumber} {a.Name}" : $"Konto #{accountId}";
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Components/Shared/JournalLinesTable.razor
git commit -m "feat: add JournalLinesTable component"
```

- [ ] **Step 4: Create the dialog component**

Create `src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor`:

```razor
@* src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor *@
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Entities
@using KoalaBooks.Domain.Enums
@using MudBlazor

<MudDialog Style="max-width:640px; width:95vw;">
    <TitleContent>
        <MudText Typo="Typo.h6">Återför verifikation #@Entry.EntryNumber</MudText>
    </TitleContent>
    <DialogContent>
        @if (_loadError is not null)
        {
            <MudAlert Severity="Severity.Error" Dense="true">@_loadError</MudAlert>
            <div style="margin-top:1rem;">
                <button class="btn btn-secondary" @onclick="OnClose">Stäng</button>
            </div>
        }
        else if (_loading)
        {
            <MudProgressLinear Color="Color.Primary" Indeterminate="true" />
        }
        else
        {
            <p style="font-size:0.8rem; color:#64748b; margin:0 0 0.75rem;">
                En bokförd verifikation kan inte tas bort — enligt BFL 5:7 / BFNAR 2013:2 rättas
                den istället genom en spegelvänd återföringsverifikation, precis som nedan.
            </p>

            <p style="margin:0 0 0.35rem; font-weight:600;">Original — #@Entry.EntryNumber, @Entry.Date</p>
            <p style="margin:0 0 0.5rem; color:#64748b; font-size:0.85rem;">@Entry.Description</p>
            <JournalLinesTable Lines="Entry.Lines" AccountsById="_accountsById" />

            <div style="text-align:center; margin:0.5rem 0; color:#94a3b8;">↓ återförs som ↓</div>

            <p style="margin:0 0 0.35rem; font-weight:600;">
                Återföring (preliminärt) — #@_preview!.EntryNumber, @_preview.Date
            </p>
            <p style="margin:0 0 0.5rem; color:#64748b; font-size:0.85rem;">
                Reversal of #@Entry.EntryNumber: @(_reason.Length > 0 ? _reason : "…")
            </p>
            <JournalLinesTable Lines="_preview.Lines" AccountsById="_accountsById" />

            <div class="form-group" style="margin-top:1rem;">
                <label>Anledning <span style="color:#ef4444;">*</span></label>
                <input type="text" @bind="_reason" placeholder="Anledning till återföring" style="width:100%;" />
            </div>

            @if (_submitError is not null)
            {
                <MudAlert Severity="Severity.Error" Dense="true" Class="mt-2">@_submitError</MudAlert>
            }

            <div style="margin-top:1rem; display:flex; gap:0.5rem;">
                <button class="btn btn-danger" @onclick="ConfirmAsync"
                        disabled="@(_saving || string.IsNullOrWhiteSpace(_reason))">
                    @(_saving ? "Bokför..." : "Bekräfta återföring")
                </button>
                <button class="btn btn-secondary" @onclick="OnClose" disabled="@_saving">Avbryt</button>
            </div>
        }
    </DialogContent>
</MudDialog>

@code {
    [Parameter, EditorRequired] public JournalEntry Entry { get; set; } = default!;
    [Parameter, EditorRequired] public List<Account> Accounts { get; set; } = [];
    [Parameter, EditorRequired] public EventCallback<JournalEntry> OnReversed { get; set; }
    [Parameter, EditorRequired] public EventCallback OnClose { get; set; }

    [Inject] private JournalEntryService JournalEntryService { get; set; } = default!;

    private Dictionary<int, Account> _accountsById = [];
    private JournalEntry? _preview;
    private string _reason = "";
    private string? _loadError;
    private string? _submitError;
    private bool _loading = true;
    private bool _saving;

    protected override async Task OnInitializedAsync()
    {
        _accountsById = Accounts.ToDictionary(a => a.Id);

        var (preview, error) = await JournalEntryService.PreviewReversalAsync(Entry.Id, "");
        _preview = preview;
        _loadError = error;
        _loading = false;
    }

    private async Task ConfirmAsync()
    {
        _submitError = null;
        _saving = true;
        try
        {
            var (result, error) = await JournalEntryService.CreateReversalAsync(Entry.Id, _reason);
            if (error is not null)
            {
                _submitError = error;
                return;
            }
            await OnReversed.InvokeAsync(result);
        }
        finally { _saving = false; }
    }
}
```

- [ ] **Step 5: Verify it compiles**

Run: `dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj`
Expected: `Build succeeded.` (Neither component is referenced by any page yet, so this only checks the files parse and compile standalone — no runtime check yet; that happens in Task 3's manual verification.)

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor
git commit -m "feat: add ReversalPreviewDialog component"
```

---

### Task 3: Wire the dialog into `Journal.razor`, remove the inline form

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:126-156` (row actions column)
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:299-312` (add dialog render after the "no entries" alert, still inside the loaded `else` block)
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:344-345` (field declarations)
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:458` (`OnFiscalYearChangedAsync` reset)
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor:470-494` (`StartReversal`/`CancelReversal`/`ConfirmReversal` methods)

**Interfaces:**
- Consumes: `ReversalPreviewDialog` (Task 2) with parameters `Entry`, `Accounts`, `OnReversed`, `OnClose`.

- [ ] **Step 1: Replace the row-actions column's reversal branch**

In `Journal.razor`, the row actions `<td>` currently starts (lines 125–133):

```razor
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
```

Replace it with (dropping the `_reversingEntryId` branch entirely, so `_convertingEntryId` becomes the leading `@if`):

```razor
                <td>
                    @if (_convertingEntryId == entry.Id)
```

Then, further down in the same `<td>` block, change (currently line 145):

```razor
                            <MudMenuItem OnClick="() => StartReversal(entry.Id)">Återför</MudMenuItem>
```

to:

```razor
                            <MudMenuItem OnClick="() => StartReversal(entry)">Återför</MudMenuItem>
```

- [ ] **Step 2: Render the dialog**

Find the "no entries" alert block (currently lines 299–311):

```razor
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

Immediately after this block (still before the closing `}` of the outer `else`), add:

```razor
@if (_reversingEntry is not null)
{
    <ReversalPreviewDialog Entry="_reversingEntry"
                           Accounts="_accounts"
                           OnReversed="OnReversedAsync"
                           OnClose="() => _reversingEntry = null" />
}
```

- [ ] **Step 3: Replace the reversal-related fields**

Change (currently lines 344–345):

```csharp
    private int? _reversingEntryId;
    private string _reversalReason = "";
```

to:

```csharp
    private JournalEntry? _reversingEntry;
```

- [ ] **Step 4: Fix the fiscal-year-change reset**

In `OnFiscalYearChangedAsync`, change (currently line 458):

```csharp
        _reversingEntryId = null;
```

to:

```csharp
        _reversingEntry = null;
```

- [ ] **Step 5: Replace the reversal methods**

Replace the existing `StartReversal`/`CancelReversal`/`ConfirmReversal` methods (currently lines 470–494):

```csharp
    private void StartReversal(int entryId)
    {
        _reversingEntryId = entryId;
        _reversalReason = "";
    }

    private void CancelReversal()
    {
        _reversingEntryId = null;
        _reversalReason = "";
    }

    private async Task ConfirmReversal(int entryId)
    {
        var (result, error) = await JournalEntryService.CreateReversalAsync(entryId, _reversalReason);
        if (error is not null)
        {
            Snackbar.Add(error, Severity.Error);
            return;
        }
        Snackbar.Add($"Återföringsverifikation #{result!.EntryNumber} skapad.", Severity.Success);
        _reversingEntryId = null;
        _reversalReason = "";
        await ReloadEntriesAsync();
    }
```

with:

```csharp
    private void StartReversal(JournalEntry entry) => _reversingEntry = entry;

    private async Task OnReversedAsync(JournalEntry reversal)
    {
        _reversingEntry = null;
        Snackbar.Add($"Återföringsverifikation #{reversal.EntryNumber} skapad.", Severity.Success);
        await ReloadEntriesAsync();
    }
```

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: `Build succeeded.` with no warnings about unused `_reversingEntryId`/`_reversalReason`/`ConfirmReversal`/`CancelReversal` (they've been fully removed, not just unreferenced).

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test`
Expected: all tests PASS (this task touches no service-layer code, so this is a pure regression check).

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "feat: replace inline storno row with ReversalPreviewDialog popup"
```

---

### Task 4: Manual browser verification

**Files:** none (verification only).

- [ ] **Step 1: Start the app**

```bash
cd src/KoalaBooks.AppHost
aspire run
```

Wait for the Aspire dashboard to report the `koalabooks-web` resource as running, then open its endpoint in a browser.

- [ ] **Step 2: Log in and set up a postable entry**

Log in with the seeded dev account (`admin@koalabooks.local` / `Admin123!`). Navigate to `/journal`. Create a new journal entry with two lines (e.g. debit an asset account, credit a revenue account, matching amounts) and post it ("Bokför"). Confirm it shows "✅ Bokförd".

- [ ] **Step 3: Open the popup and verify the preview**

Open the row's ⋮ menu → "Återför". Confirm:
- The popup opens (not the old inline reason box).
- It shows the **original** entry's number, date, description, and both lines with correct account names/amounts.
- Below a "↓ återförs som ↓" divider, it shows a **preview** reversal with a provisional entry number one higher than any existing entry, a date, and lines with debit/credit swapped relative to the original.
- The "Bekräfta återföring" button is disabled while the reason field is empty.

- [ ] **Step 4: Type a reason and confirm live update**

Type a reason into the field (e.g. "Fel belopp"). Confirm the reversal preview's description line updates live to show `Reversal of #<N>: Fel belopp`, and the confirm button becomes enabled.

- [ ] **Step 5: Confirm and verify the result**

Click "Bekräfta återföring". Confirm:
- The popup closes.
- A success snackbar appears naming the new reversal's entry number.
- The original row now shows "↩️ Återförd" with no ⋮ menu (no further actions).
- A new row appears showing "🔄 Rättelse" with the mirrored amounts, matching what the preview showed.

- [ ] **Step 6: Verify Avbryt does nothing**

Repeat steps 2–3 on a different posted entry, then click "Avbryt" instead of confirming. Confirm the popup closes and the entry is unchanged (still "✅ Bokförd", no new reversal entry created).

No commit for this task — it's verification only. If any step fails, return to the relevant earlier task, fix, and re-run this task's steps from the start.
