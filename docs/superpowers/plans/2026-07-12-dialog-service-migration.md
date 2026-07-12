# Migrate popup dialogs to IDialogService/MudDialogProvider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert `ReversalPreviewDialog`, `ClassifyDocumentDialog`, and `PreviewDocumentDialog` (and their call sites in `Journal.razor`/`Inbox.razor`) from the hand-rolled `@if`-rendered `<MudDialog @ref Visible="true">` convention to MudBlazor's `IDialogService`/`MudDialogProvider`, fixing the two dialogs (`ClassifyDocumentDialog`, `PreviewDocumentDialog`) that are currently non-functional because they never got the `@ref`/`Visible`/`CloseAsync()` fix `ReversalPreviewDialog` received in #210.

**Architecture:** Each dialog drops `@ref`/`Visible="true"` from its root `<MudDialog>` tag and receives `[CascadingParameter] IMudDialogInstance MudDialog` instead. Parent pages drop their `_xxxDoc`/`_reversingEntry` fields and `@if` blocks, and instead call `DialogService.ShowAsync<TDialog>(title, parameters, DialogDefaults.NoDismiss)`, awaiting `IDialogReference.Result` to react to success/cancel. `App.razor` already hosts `<MudDialogProvider>`; no change needed there.

**Tech Stack:** Blazor Server (.NET 10, `net10.0`), MudBlazor 9.6.0, `KoalaBooks.Components` Razor class library.

## Global Constraints

- Behavior (what each dialog does, what data it needs, what happens on success/cancel) is preserved exactly — only the hosting mechanism changes.
- All three dialogs use `DialogDefaults.NoDismiss` (`BackdropClick = false, CloseOnEscapeKey = false`) — no dialog may be dismissed by backdrop click or Escape.
- The `_saving` re-entrancy guard in each dialog (`if (_saving) return;` / `disabled="@_saving"` on confirm buttons) is untouched — do not modify it in any task below.
- No service-layer code changes (`JournalEntryService`, `DocumentService`, etc.).
- No new automated test coverage — no bUnit package exists in this repo; verification is `dotnet build` plus manual Playwright browser testing, matching the existing convention for these dialogs (see spec's Testing section).
- Branch `dialog-service-refactor` was created off `dialog-actions-component` (#215, draft, unmerged) — do not rebase onto or merge from `main` during this work.

---

## File Structure

- `src/KoalaBooks.Components/Shared/DialogDefaults.cs` (new) — shared `DialogOptions` constant used by every `ShowAsync` call site.
- `src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor` (modify) — drops manual close plumbing, adds cascading `MudDialog`.
- `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor` (modify) — same conversion; this is one of the two currently-broken dialogs.
- `src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor` (modify) — same conversion plus a new `PreviewOutcome` enum so its parent can distinguish "saved" from "hand off to classify" without a second callback.
- `src/KoalaBooks.Components/Pages/Journal.razor` (modify) — call site for `ReversalPreviewDialog`.
- `src/KoalaBooks.Components/Pages/Inbox.razor` (modify) — call site for `ClassifyDocumentDialog` and `PreviewDocumentDialog`, including the preview→classify chain.

Task grouping rationale: a dialog component and its parent call site must change together — the component's build won't succeed (removed `[Parameter]`s) until the call site stops passing them, and vice versa. So each task below pairs component + call site file(s) that must land in the same commit to keep the tree buildable at every commit.

---

### Task 1: Shared `DialogDefaults.NoDismiss` options

**Files:**
- Create: `src/KoalaBooks.Components/Shared/DialogDefaults.cs`

**Interfaces:**
- Produces: `KoalaBooks.Components.Shared.DialogDefaults.NoDismiss` (a `MudBlazor.DialogOptions` with `BackdropClick = false, CloseOnEscapeKey = false`), consumed by Task 2 and Task 3's `ShowAsync` call sites.

- [ ] **Step 1: Create the file**

```csharp
// src/KoalaBooks.Components/Shared/DialogDefaults.cs
using MudBlazor;

namespace KoalaBooks.Components.Shared;

public static class DialogDefaults
{
    public static readonly DialogOptions NoDismiss = new()
    {
        BackdropClick = false,
        CloseOnEscapeKey = false
    };
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build KoalaBooks.slnx`
Expected: `Build succeeded.` (this file has no callers yet, so it just needs to compile standalone)

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Components/Shared/DialogDefaults.cs
git commit -m "feat: add shared no-dismiss dialog options for IDialogService migration"
```

---

### Task 2: Convert `ReversalPreviewDialog` + `Journal.razor` call site

**Files:**
- Modify: `src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor`
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`

**Interfaces:**
- Consumes: `KoalaBooks.Components.Shared.DialogDefaults.NoDismiss` (Task 1).
- Produces: `ReversalPreviewDialog` closes via `MudDialog.Close(DialogResult.Ok(result))` where `result` is the created `JournalEntry` (from `JournalEntryService.CreateReversalAsync`), or `MudDialog.Cancel()` on every dismissal path. `Journal.razor`'s `StartReversalAsync(JournalEntry entry)` is the only call site — no other file references `ReversalPreviewDialog` or its removed `OnReversed`/`OnClose` parameters.

- [ ] **Step 1: Convert `ReversalPreviewDialog.razor`'s root tag and dismissal buttons**

In `src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor`, replace:

```razor
<MudDialog @ref="_dialog" Visible="true" Options="_dialogOptions" Style="max-width:640px; width:95vw;">
```

with:

```razor
<MudDialog Style="max-width:640px; width:95vw;">
```

Replace:

```razor
                <button class="btn btn-secondary" @onclick="CloseAsync">Stäng</button>
```

with:

```razor
                <button class="btn btn-secondary" @onclick="MudDialog.Cancel">Stäng</button>
```

Replace:

```razor
                <button class="btn btn-secondary" @onclick="CloseAsync" disabled="@_saving">Avbryt</button>
```

with:

```razor
                <button class="btn btn-secondary" @onclick="MudDialog.Cancel" disabled="@_saving">Avbryt</button>
```

- [ ] **Step 2: Convert `ReversalPreviewDialog.razor`'s `@code` block**

Replace the entire `@code { ... }` block (current lines 73–134) with:

```csharp
@code {
    [Parameter, EditorRequired] public JournalEntry Entry { get; set; } = default!;
    [Parameter, EditorRequired] public List<Account> Accounts { get; set; } = [];

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
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
        if (_saving)
        {
            return;
        }

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
            MudDialog.Close(DialogResult.Ok(result));
        }
        finally { _saving = false; }
    }
}
```

This removes the `OnReversed`/`OnClose` parameters, the `_dialog` field, the `_dialogOptions` field (now superseded by the caller-supplied `DialogDefaults.NoDismiss` from Task 1), and the `CloseAsync()` helper.

- [ ] **Step 3: Update `Journal.razor`'s injects**

In `src/KoalaBooks.Components/Pages/Journal.razor`, replace:

```razor
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
```

with:

```razor
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
```

- [ ] **Step 4: Remove the `@if` block that rendered the dialog**

In `src/KoalaBooks.Components/Pages/Journal.razor`, delete these lines entirely:

```razor
@if (_reversingEntry is not null)
{
    <ReversalPreviewDialog Entry="_reversingEntry"
                           Accounts="_accounts"
                           OnReversed="OnReversedAsync"
                           OnClose="() => _reversingEntry = null" />
}
```

(leaving the `}` that closes the surrounding `else { ... }` block on its own line, unchanged, immediately after)

- [ ] **Step 5: Remove the `_reversingEntry` field**

In `src/KoalaBooks.Components/Pages/Journal.razor`, delete this line:

```razor
    private JournalEntry? _reversingEntry;
```

- [ ] **Step 6: Remove the now-dangling reset in `OnFiscalYearChangedAsync`**

In `src/KoalaBooks.Components/Pages/Journal.razor`, in `OnFiscalYearChangedAsync`, replace:

```csharp
        _convertingEntryId = null;
        _reversingEntry = null;
        _isReloading = true;
```

with:

```csharp
        _convertingEntryId = null;
        _isReloading = true;
```

(the dialog is no longer parent-owned state, so there's nothing to null out here — an in-flight `ReversalPreviewDialog` now manages its own lifecycle independent of fiscal-year selection)

- [ ] **Step 7: Update the "Återför" menu item and replace `StartReversal`/`OnReversedAsync`**

In `src/KoalaBooks.Components/Pages/Journal.razor`, replace:

```razor
                            <MudMenuItem OnClick="() => StartReversal(entry)">Återför</MudMenuItem>
```

with:

```razor
                            <MudMenuItem OnClick="() => StartReversalAsync(entry)">Återför</MudMenuItem>
```

Then replace:

```csharp
    private void StartReversal(JournalEntry entry) => _reversingEntry = entry;

    private async Task OnReversedAsync(JournalEntry reversal)
    {
        _reversingEntry = null;
        Snackbar.Add($"Återföringsverifikation #{reversal.EntryNumber} skapad.", Severity.Success);
        await ReloadEntriesAsync();
    }
```

with:

```csharp
    private async Task StartReversalAsync(JournalEntry entry)
    {
        var parameters = new DialogParameters<ReversalPreviewDialog>
        {
            { x => x.Entry, entry },
            { x => x.Accounts, _accounts }
        };
        var dialogRef = await DialogService.ShowAsync<ReversalPreviewDialog>(
            $"Återför verifikation #{entry.EntryNumber}", parameters, DialogDefaults.NoDismiss);
        var result = await dialogRef.Result;
        if (result is { Canceled: false } && result.Data is JournalEntry reversal)
        {
            Snackbar.Add($"Återföringsverifikation #{reversal.EntryNumber} skapad.", Severity.Success);
            await ReloadEntriesAsync();
        }
    }
```

- [ ] **Step 8: Build to verify**

Run: `dotnet build KoalaBooks.slnx`
Expected: `Build succeeded.` — this is the only automated check available (no bUnit tests exist for these dialogs); if it fails, check for a leftover reference to `OnReversed`/`OnClose`/`_reversingEntry`/`_dialog`/`_dialogOptions`/`CloseAsync` anywhere in the two files.

- [ ] **Step 9: Manual browser check**

Run: `aspire start` (or the project's normal dev-server startup) then open `/journal`, pick a posted entry's row menu → "Återför". Confirm:
- The dialog opens showing the original entry and the provisional reversal.
- Typing a reason updates the live description.
- Backdrop click and Escape do **not** close the dialog.
- "Avbryt" closes it with no reversal created.
- "Bekräfta återföring" creates the reversal, closes the dialog, shows the success snackbar, and the row updates to "Återförd".

- [ ] **Step 10: Commit**

```bash
git add src/KoalaBooks.Components/Shared/ReversalPreviewDialog.razor src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "refactor: migrate ReversalPreviewDialog to IDialogService"
```

---

### Task 3: Convert `ClassifyDocumentDialog` + `PreviewDocumentDialog` + `Inbox.razor` call site

**Files:**
- Modify: `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor`
- Modify: `src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor`
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`

**Interfaces:**
- Consumes: `KoalaBooks.Components.Shared.DialogDefaults.NoDismiss` (Task 1).
- Produces: `ClassifyDocumentDialog` closes via `MudDialog.Close()` (no payload) on any of its three classify-success paths, or `MudDialog.Cancel()` on "Avbryt". `PreviewDocumentDialog` exposes `public enum PreviewOutcome { Saved, Classify }` and closes via `MudDialog.Close(DialogResult.Ok(PreviewOutcome.Saved))` on save, `MudDialog.Close(DialogResult.Ok(PreviewOutcome.Classify))` on "Bokför", or `MudDialog.Cancel()` on "Avbryt". `Inbox.razor`'s `OpenClassifyDialogAsync(DocumentMeta)` and `OpenPreviewDialogAsync(DocumentMeta)` are the only call sites — `OpenPreviewDialogAsync` calls `OpenClassifyDialogAsync` directly when it observes `PreviewOutcome.Classify`, replacing the old field-chaining.

- [ ] **Step 1: Convert `ClassifyDocumentDialog.razor`'s "Avbryt" button**

In `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor`, replace:

```razor
                    <button class="btn btn-secondary" @onclick="OnClose">Avbryt</button>
```

with:

```razor
                    <button class="btn btn-secondary" @onclick="MudDialog.Cancel">Avbryt</button>
```

(the root `<MudDialog Style="max-width:900px; width:95vw;">` tag is already free of `@ref`/`Visible`/`Options` — no change needed there)

- [ ] **Step 2: Convert `ClassifyDocumentDialog.razor`'s parameters and cascading param**

Replace:

```csharp
    [Parameter, EditorRequired] public DocumentMeta Doc { get; set; } = default!;
    [Parameter, EditorRequired] public IDocumentProvider DocumentProvider { get; set; } = default!;
    [Parameter, EditorRequired] public EventCallback OnClassified { get; set; }
    [Parameter, EditorRequired] public EventCallback OnClose { get; set; }

    [Inject] private DocumentService DocumentService { get; set; } = default!;
```

with:

```csharp
    [Parameter, EditorRequired] public DocumentMeta Doc { get; set; } = default!;
    [Parameter, EditorRequired] public IDocumentProvider DocumentProvider { get; set; } = default!;

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Inject] private DocumentService DocumentService { get; set; } = default!;
```

- [ ] **Step 3: Replace the three `OnClassified.InvokeAsync()` call sites**

In `ClassifyAsSupplierInvoiceAsync`, replace:

```csharp
        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.SupplierInvoice, created!.Id);
        await OnClassified.InvokeAsync();
    }

    private async Task ClassifyAsCustomerInvoiceAsync()
```

with:

```csharp
        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.SupplierInvoice, created!.Id);
        MudDialog.Close();
    }

    private async Task ClassifyAsCustomerInvoiceAsync()
```

In `ClassifyAsCustomerInvoiceAsync`, replace:

```csharp
        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.CustomerInvoice, created!.Id);
        await OnClassified.InvokeAsync();
    }

    private async Task ClassifyAsJournalEntryAsync()
```

with:

```csharp
        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.CustomerInvoice, created!.Id);
        MudDialog.Close();
    }

    private async Task ClassifyAsJournalEntryAsync()
```

In `ClassifyAsJournalEntryAsync`, replace:

```csharp
            await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.JournalEntry, _existingEntryId);
            await OnClassified.InvokeAsync();
            return;
        }
```

with:

```csharp
            await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.JournalEntry, _existingEntryId);
            MudDialog.Close();
            return;
        }
```

and replace:

```csharp
        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.JournalEntry, created!.Id);
        await OnClassified.InvokeAsync();
    }
}
```

with:

```csharp
        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.JournalEntry, created!.Id);
        MudDialog.Close();
    }
}
```

- [ ] **Step 4: Convert `PreviewDocumentDialog.razor`'s buttons**

In `src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor`, replace:

```razor
                    <button class="btn btn-secondary" @onclick="OnClose">Avbryt</button>
                    <button class="btn btn-secondary" @onclick="OpenClassifyAsync">Bokför</button>
```

with:

```razor
                    <button class="btn btn-secondary" @onclick="MudDialog.Cancel">Avbryt</button>
                    <button class="btn btn-secondary" @onclick="OpenClassify">Bokför</button>
```

(the root `<MudDialog Style="max-width:900px; width:95vw;">` tag already has no `@ref`/`Visible`/`Options` — no change needed there)

- [ ] **Step 5: Convert `PreviewDocumentDialog.razor`'s `@code` block**

Replace:

```csharp
@code {
    [Parameter, EditorRequired] public DocumentMeta Doc { get; set; } = default!;
    [Parameter, EditorRequired] public IDocumentProvider DocumentProvider { get; set; } = default!;
    [Parameter, EditorRequired] public EventCallback OnSaved { get; set; }
    [Parameter, EditorRequired] public EventCallback OnClose { get; set; }
    [Parameter, EditorRequired] public EventCallback OnClassify { get; set; }

    [Inject] private DocumentService DocumentService { get; set; } = default!;
```

with:

```csharp
@code {
    public enum PreviewOutcome { Saved, Classify }

    [Parameter, EditorRequired] public DocumentMeta Doc { get; set; } = default!;
    [Parameter, EditorRequired] public IDocumentProvider DocumentProvider { get; set; } = default!;

    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Inject] private DocumentService DocumentService { get; set; } = default!;
```

Then replace:

```csharp
            var err = await DocumentService.UpdateMetadataAsync(Doc.Id, type, date);
            if (err is not null) { _error = err; return; }
            await OnSaved.InvokeAsync();
        }
        finally { _saving = false; }
    }

    private async Task OpenClassifyAsync() => await OnClassify.InvokeAsync();
}
```

with:

```csharp
            var err = await DocumentService.UpdateMetadataAsync(Doc.Id, type, date);
            if (err is not null) { _error = err; return; }
            MudDialog.Close(DialogResult.Ok(PreviewOutcome.Saved));
        }
        finally { _saving = false; }
    }

    private void OpenClassify() => MudDialog.Close(DialogResult.Ok(PreviewOutcome.Classify));
}
```

- [ ] **Step 6: Update `Inbox.razor`'s injects**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, replace:

```razor
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
```

with:

```razor
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
```

- [ ] **Step 7: Remove the two `@if` blocks and the row buttons' targets**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, delete these lines entirely:

```razor
@if (_classifyDoc is not null)
{
    <ClassifyDocumentDialog Doc="_classifyDoc"
                            DocumentProvider="DocumentProvider"
                            OnClassified="OnDocumentClassified"
                            OnClose="() => _classifyDoc = null" />
}

@if (_previewDoc is not null)
{
    <PreviewDocumentDialog Doc="_previewDoc"
                           DocumentProvider="DocumentProvider"
                           OnSaved="OnDocumentSaved"
                           OnClose="() => _previewDoc = null"
                           OnClassify="OpenClassifyFromPreview" />
}
```

Replace the row action buttons:

```razor
                            <button class="btn btn-sm btn-secondary" @onclick="() => OpenPreviewDialog(doc)" title="Förhandsgranska">👁</button>
                            <button class="btn btn-sm btn-primary" @onclick="() => OpenClassifyDialog(doc)">Bokför</button>
```

with:

```razor
                            <button class="btn btn-sm btn-secondary" @onclick="() => OpenPreviewDialogAsync(doc)" title="Förhandsgranska">👁</button>
                            <button class="btn btn-sm btn-primary" @onclick="() => OpenClassifyDialogAsync(doc)">Bokför</button>
```

- [ ] **Step 8: Remove the `_classifyDoc`/`_previewDoc` fields**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, replace:

```csharp
    private string _filter = "all";
    private DocumentMeta? _classifyDoc;
    private string _sortBy = "uploadedAt";
    private bool _sortAsc = false;
    private DocumentMeta? _previewDoc;
    private int _page = 1;
```

with:

```csharp
    private string _filter = "all";
    private string _sortBy = "uploadedAt";
    private bool _sortAsc = false;
    private int _page = 1;
```

- [ ] **Step 9: Replace the five dialog-opening/callback methods**

Replace:

```csharp
    private void OpenClassifyDialog(DocumentMeta doc) => _classifyDoc = doc;

    private async Task OnDocumentClassified()
    {
        _classifyDoc = null;
        await LoadPageAsync();
        Snackbar.Add("Dokument klassificerat.", Severity.Success);
    }

    private void OpenPreviewDialog(DocumentMeta doc) => _previewDoc = doc;

    private async Task OnDocumentSaved()
    {
        _previewDoc = null;
        await LoadPageAsync();
        Snackbar.Add("Sparat.", Severity.Success);
    }

    private void OpenClassifyFromPreview()
    {
        var doc = _previewDoc;
        _previewDoc = null;
        _classifyDoc = doc;
    }
```

with:

```csharp
    private async Task OpenClassifyDialogAsync(DocumentMeta doc)
    {
        var parameters = new DialogParameters<ClassifyDocumentDialog>
        {
            { x => x.Doc, doc },
            { x => x.DocumentProvider, DocumentProvider }
        };
        var dialogRef = await DialogService.ShowAsync<ClassifyDocumentDialog>(
            "Klassificera dokument", parameters, DialogDefaults.NoDismiss);
        var result = await dialogRef.Result;
        if (result is { Canceled: false })
        {
            await LoadPageAsync();
            Snackbar.Add("Dokument klassificerat.", Severity.Success);
        }
    }

    private async Task OpenPreviewDialogAsync(DocumentMeta doc)
    {
        var parameters = new DialogParameters<PreviewDocumentDialog>
        {
            { x => x.Doc, doc },
            { x => x.DocumentProvider, DocumentProvider }
        };
        var dialogRef = await DialogService.ShowAsync<PreviewDocumentDialog>(
            "Förhandsgranskning", parameters, DialogDefaults.NoDismiss);
        var result = await dialogRef.Result;
        if (result is { Canceled: false } && result.Data is PreviewDocumentDialog.PreviewOutcome outcome)
        {
            if (outcome == PreviewDocumentDialog.PreviewOutcome.Classify)
            {
                await OpenClassifyDialogAsync(doc);
            }
            else
            {
                await LoadPageAsync();
                Snackbar.Add("Sparat.", Severity.Success);
            }
        }
    }
```

- [ ] **Step 10: Build to verify**

Run: `dotnet build KoalaBooks.slnx`
Expected: `Build succeeded.` If it fails, check for a leftover reference to `OnClassified`/`OnClose`/`OnSaved`/`OnClassify`/`_classifyDoc`/`_previewDoc`/`OpenClassifyDialog`/`OpenPreviewDialog`/`OnDocumentClassified`/`OnDocumentSaved`/`OpenClassifyFromPreview`/`OpenClassifyAsync` anywhere in the three files.

- [ ] **Step 11: Manual browser check**

Open `/inbox` and, for a document row:
- Click 👁 (preview): dialog opens, showing the document and metadata form. Backdrop click and Escape do **not** close it (this is the regression check — this dialog was previously non-functional).
- Click "Bokför" inside the preview dialog: preview dialog closes and the Classify dialog opens for the *same* document.
- Cancel the Classify dialog, reopen preview, click "Spara": dialog closes, page reloads, "Sparat." snackbar shown.
- Click "Bokför" directly from the row (not via preview): Classify dialog opens directly. Fill in a valid Leverantörsfaktura and click "Skapa & koppla": dialog closes, page reloads, "Dokument klassificerat." snackbar shown, row shows the new type badge.
- "Avbryt" on both dialogs closes with no changes made.

- [ ] **Step 12: Commit**

```bash
git add src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor src/KoalaBooks.Components/Pages/Inbox.razor
git commit -m "fix: migrate ClassifyDocumentDialog and PreviewDocumentDialog to IDialogService"
```

---

### Task 4: Full regression pass

**Files:** none (verification only)

**Interfaces:** none — this task only runs existing build/test/manual checks across all changes from Tasks 1–3.

- [ ] **Step 1: Run the full build**

Run: `dotnet build KoalaBooks.slnx`
Expected: `Build succeeded.` with zero warnings introduced by these changes (pre-existing warnings, if any, are out of scope).

- [ ] **Step 2: Run the existing automated test suite**

Run: `dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj`
Expected: all tests pass, in particular `ReversalDateClampingTests`, `ReversalClosedYearTests`, `JournalEntryStatusTests`, `AuditTrailTests`, `JournalEntryDbGuardTests` — none of these should be affected since no service-layer code changed, but this confirms nothing else broke.

- [ ] **Step 3: Manual Playwright walkthrough of all three dialogs in one session**

Using the `playwright-cli` skill (or manual browser interaction if unavailable), in a single running session:
1. `/journal` → row menu → "Återför" → confirm reversal creation still works (already covered in Task 2, re-verify here to catch any cross-page regression).
2. `/inbox` → 👁 preview → "Bokför" → classify → confirm the full chain still works end-to-end.
3. Open each of the three dialogs and confirm none can be dismissed via backdrop click or Escape key.
4. Open two dialogs in sequence (not simultaneously) and confirm each properly cleans up — no leftover overlay, no stuck "open" state, page fully interactive after each close.

- [ ] **Step 4: Push branch and open draft PR**

```bash
git push -u origin dialog-service-refactor
gh pr create --draft --base dialog-actions-component --title "Migrate popup dialogs to IDialogService/MudDialogProvider" --body "$(cat <<'EOF'
## Summary
- Converts ReversalPreviewDialog, ClassifyDocumentDialog, and PreviewDocumentDialog from the hand-rolled `@if`-rendered `<MudDialog @ref Visible="true">` convention to IDialogService/MudDialogProvider.
- Fixes ClassifyDocumentDialog and PreviewDocumentDialog, which were non-functional (never received the @ref/Visible/CloseAsync fix ReversalPreviewDialog got in #210).

## Test plan
- [x] `dotnet build` succeeds
- [x] `dotnet test` passes (service-layer tests unaffected)
- [x] Manual: Återför flow on /journal
- [x] Manual: preview → Bokför → classify chain on /inbox
- [x] Manual: backdrop-click/Escape no longer dismiss any of the three dialogs

Targets `dialog-actions-component` (#215, draft) since both branches touch the same three dialog files — avoids a guaranteed merge conflict. Will auto-retarget to main once #215 merges.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Note: per the spec's Branch/PR strategy, this targets `dialog-actions-component`, not `main`. If `#215` has picked up new commits since this branch was created, rebase onto its current tip before pushing.

---

## Self-Review

**Spec coverage:**
- Shared `DialogDefaults.NoDismiss` → Task 1.
- `ReversalPreviewDialog` conversion (remove `@ref`/`Visible`/`Options`, cascading param, `Close(DialogResult.Ok(result))`, `Cancel()` on both dismissal paths) → Task 2, Steps 1–2.
- `ClassifyDocumentDialog` conversion (three `Close()` call sites, `Cancel()`) → Task 3, Steps 1–3.
- `PreviewDocumentDialog` conversion (`PreviewOutcome` enum, `Close(DialogResult.Ok(...))` both branches, `Cancel()`) → Task 3, Steps 4–5.
- `Journal.razor` call site (`StartReversalAsync`, inject, field/block removal, `OnFiscalYearChangedAsync` cleanup, menu item) → Task 2, Steps 3–7.
- `Inbox.razor` call site (`OpenClassifyDialogAsync`/`OpenPreviewDialogAsync`, inject, field/block removal, row buttons) → Task 3, Steps 6–9.
- "What this refactor won't fix" (`_saving` guard) → explicitly called out in Global Constraints; no task touches it.
- Testing section (manual Playwright, `dotnet build`/`dotnet test`) → Task 2 Step 9, Task 3 Step 11, Task 4.
- Branch/PR strategy (target `dialog-actions-component`) → Task 4, Step 4.
- Out of scope (`App.razor`, service layer, no new automated tests) → not touched by any task.

**Placeholder scan:** none found — every step has literal code, exact commands, and expected output.

**Type consistency:** `PreviewDocumentDialog.PreviewOutcome` (defined Task 3 Step 5) matches its usage in `Inbox.razor`'s `OpenPreviewDialogAsync` (Task 3 Step 9) exactly, including the fully-qualified `PreviewDocumentDialog.PreviewOutcome.Classify`/`.Saved` references. `DialogDefaults.NoDismiss` (Task 1) is referenced identically in both `StartReversalAsync` (Task 2) and both `Inbox.razor` methods (Task 3). `DialogParameters<T>` generic usage matches each dialog's actual `[Parameter]` names (`Entry`/`Accounts` for `ReversalPreviewDialog`; `Doc`/`DocumentProvider` for the other two).
