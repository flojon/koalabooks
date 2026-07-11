# Migrate popup dialogs to IDialogService/MudDialogProvider (#210/#215 follow-up)

## Problem

The codebase's three popup dialogs (`ReversalPreviewDialog`, `ClassifyDocumentDialog`,
`PreviewDocumentDialog`) use a hand-rolled convention: they're conditionally rendered
by the parent page (`@if (_state is not null) { <SomeDialog ... /> }`) with an inline
`<MudDialog>` as their root markup, rather than going through MudBlazor's
`IDialogService`/`MudDialogProvider`.

That convention has a sharp edge, discovered the hard way in #210:
`<MudDialog>` used this way only renders if `Visible="true"` is explicitly set (its
internal `ShowAsync()` fires off that parameter), and only closes correctly if the
component holds its own `@ref` and calls `_dialog.CloseAsync()` — removing the
component from the parent's render tree is not enough, and races with the dialog's
own internally-tracked reference, leaving it stuck open. `ReversalPreviewDialog` got
this fixed after manual browser testing surfaced both failures. `ClassifyDocumentDialog`
and `PreviewDocumentDialog` never got the fix — they still lack `Visible="true"` and
`@ref`/`CloseAsync()`, so per the same confirmed mechanism they are currently
non-functional in the running app (flagged as a known follow-up in #210, and
independently reproduced while investigating this).

Patching the two broken dialogs with the same manual pattern would fix the immediate
symptom but leaves the footgun in place for the next dialog anyone adds. This spec
instead moves all three to the standard `IDialogService`/`MudDialogProvider` model,
which owns showing, hiding, backdrop/Escape handling, and stacking itself — there is
no `Visible` flag to forget and no manual close-reference plumbing to get wrong.

## Goal

Convert `ReversalPreviewDialog`, `ClassifyDocumentDialog`, and `PreviewDocumentDialog`
(and their call sites in `Journal.razor` and `Inbox.razor`) to open via
`IDialogService.ShowAsync<T>(...)` and close via the injected `IMudDialogInstance`,
eliminating the `Visible`/`@ref`/`CloseAsync()` pattern entirely. Behavior (what each
dialog does, what data it needs, what happens on success/cancel) is preserved exactly;
only the hosting mechanism changes.

`App.razor` already registers `<MudDialogProvider @rendermode="InteractiveServer" />`
(line 22) — it's unused today but requires no change.

## Architecture change

Each dialog's root markup drops `@ref` and `Visible="true"` from its `<MudDialog>` tag
(everything else about that tag — `Style`, `TitleContent`, `DialogContent` — stays as
today). Inside `@code`, the manual `private MudDialog _dialog` field is replaced with:

```csharp
[CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
```

Parents stop holding `_classifyDoc`/`_previewDoc`/`_reversingEntry` fields and their
`@if` blocks. A button handler instead calls `DialogService.ShowAsync<TDialog>(...)`,
awaits the returned `IDialogReference`'s `.Result`, and reacts to the outcome. Each
page injects `[Inject] private IDialogService DialogService { get; set; } = default!;`
(not currently used anywhere in the codebase).

### Shared dialog options

`ReversalPreviewDialog` currently disables `BackdropClick`/`CloseOnEscapeKey` via a
static `DialogOptions` field passed to its own `<MudDialog Options="...">`, to stop
dismissal paths from bypassing `CloseAsync()` and leaking stale preview state across
entries. Under `IDialogService`, options are supplied by the *caller* to `ShowAsync`,
not by the dialog component itself. `ClassifyDocumentDialog`/`PreviewDocumentDialog`
never had this protection (they were never functionally exercised), but the same
hazard — backdrop click during an in-flight save — applies to them once they work, so
all three call sites use the same options.

New file `KoalaBooks.Components/Shared/DialogDefaults.cs`:

```csharp
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

## Component changes

### `ReversalPreviewDialog.razor`

- Remove `[Parameter] EventCallback<JournalEntry> OnReversed`, `[Parameter] EventCallback OnClose`, `_dialog` field, and the `CloseAsync()` helper method.
- Add the `MudDialog` cascading parameter (above).
- `ConfirmAsync`: on success, replace `await _dialog.CloseAsync(); await OnReversed.InvokeAsync(result);` with `MudDialog.Close(DialogResult.Ok(result));` (`result` is the created `JournalEntry` reversal — the parent needs its entry number for the success toast).
- Every dismissal path (the "Stäng" button shown on `_loadError`, and the "Avbryt" button) calls `MudDialog.Cancel()` instead of the current `CloseAsync` wrapper.
- `Entry`/`Accounts` parameters are unchanged; they're now supplied via `DialogParameters` instead of Razor attributes (component-side, no difference).

### `ClassifyDocumentDialog.razor`

- Remove `[Parameter] EventCallback OnClassified`, `[Parameter] EventCallback OnClose`.
- Add the `MudDialog` cascading parameter.
- The three `ClassifyAsXxxAsync` success paths (`SupplierInvoice`/`CustomerInvoice`/`JournalEntry`) replace `await OnClassified.InvokeAsync();` with `MudDialog.Close();` (no payload needed — the parent only cares that it wasn't canceled).
- "Avbryt" button calls `MudDialog.Cancel()` instead of `OnClose`.

### `PreviewDocumentDialog.razor`

This one has a real branch: "Bokför" hands the document off to the Classify dialog
instead of just closing. Add a public nested result enum so the parent can distinguish
outcomes without a second callback parameter:

```csharp
public enum PreviewOutcome { Saved, Classify }
```

- Remove `[Parameter] EventCallback OnSaved`, `[Parameter] EventCallback OnClose`, `[Parameter] EventCallback OnClassify`.
- Add the `MudDialog` cascading parameter.
- `SaveAsync`: on success, replace `await OnSaved.InvokeAsync();` with `MudDialog.Close(DialogResult.Ok(PreviewOutcome.Saved));`.
- `OpenClassifyAsync` (today: `async Task OpenClassifyAsync() => await OnClassify.InvokeAsync();`) becomes `void OpenClassify() => MudDialog.Close(DialogResult.Ok(PreviewOutcome.Classify));` — the "Bokför" button's `@onclick="OpenClassifyAsync"` markup reference is updated to `@onclick="OpenClassify"` to match.
- "Avbryt" button calls `MudDialog.Cancel()` instead of `OnClose`.

## Call site changes

### `Journal.razor`

- Remove the `_reversingEntry` field and the `@if (_reversingEntry is not null) { <ReversalPreviewDialog ... /> }` block (current lines 305–311).
- `StartReversal(JournalEntry entry)` becomes:

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

  (The `ShowAsync` title argument is a fallback only used if `TitleContent` isn't set;
  the component still defines its own `TitleContent`, so the string passed here is
  never actually displayed — kept for clarity/accessibility only.)
- The `MudMenuItem` "Återför" `OnClick` changes from `() => StartReversal(entry.Id)` to `() => StartReversalAsync(entry)`.
- `OnReversedAsync(JournalEntry)` is removed — folded into the block above.

### `Inbox.razor`

- Remove the `_classifyDoc`/`_previewDoc` fields and both `@if` blocks (current lines 113–128).
- `OpenClassifyDialog(DocumentMeta doc)` (line 262) becomes:

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
```

- `OpenPreviewDialog(DocumentMeta doc)` (line 271) becomes:

```csharp
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

  This replaces today's `OpenClassifyFromPreview()` chaining (`_previewDoc = null;
  _classifyDoc = doc;`), which is removed entirely — the chain is now two sequential
  awaited `ShowAsync` calls instead of two conditionally-rendered components toggling
  each other's backing fields.
- The row buttons (lines 89–90) change from `OpenPreviewDialog(doc)`/`OpenClassifyDialog(doc)` to `OpenPreviewDialogAsync(doc)`/`OpenClassifyDialogAsync(doc)`.
- Both pages add `[Inject] private IDialogService DialogService { get; set; } = default!;`.

## What this refactor won't fix

The `_saving` re-entrancy guard in each dialog's submit handler (`if (_saving) return;`
in `ReversalPreviewDialog.ConfirmAsync`, `_saving = true` around the classify/save
calls in the other two, each paired with `disabled="@_saving"` on the confirm button)
is a double-click/double-submit race, orthogonal to which hosting pattern is used.
Switching to `IDialogService` does not disable the confirm button between click and
dialog-close on its own — that guard stays exactly as it is today in all three
components. Nothing in this refactor touches it.

## Error handling

Unchanged from today, just relocated:

- `ReversalPreviewDialog`'s `_loadError` (preview-fetch failure) and `_submitError`
  (confirm failure) still render inline `MudAlert`s inside `DialogContent`; only the
  dismissal call (`OnClose.InvokeAsync()` → `MudDialog.Cancel()`) changes.
- `ClassifyDocumentDialog`/`PreviewDocumentDialog`'s `_error` field and per-field
  validation messages (e.g. "Leverantör är obligatoriskt.") are untouched — they
  block `_error` from being null and keep the dialog open, exactly as today.
- Backdrop-click/Escape-key dismissal is disabled for all three (`DialogDefaults.NoDismiss`), so the only ways out are the explicit buttons, which already route through the correct close/cancel path per dialog above.

## Testing

No bUnit/component-level tests exist for these dialogs today (confirmed: no `bUnit`
package reference in either project, no test files matching `*ReversalPreview*`,
`*ClassifyDocument*`, or `*PreviewDocument*`). Verification has been manual browser
testing via Playwright, matching the pattern already used in #174/#210. This refactor
follows the same convention rather than introducing new automated coverage:

- `ReversalPreviewDialog`: open via row menu → Återför, confirm it renders (fixing
  nothing here — it already works), confirm/cancel/backdrop-click all behave as
  before.
- `ClassifyDocumentDialog`/`PreviewDocumentDialog`: open via Inbox row buttons
  (👁 / Bokför) — this is the actual regression check, since these two are currently
  broken. Confirm they now render, confirm Spara/Skapa & koppla/Avbryt all work,
  confirm "Bokför" from the Preview dialog correctly opens the Classify dialog for the
  same document, confirm backdrop-click/Escape no longer dismiss any of the three.
- Existing service-layer tests (`ReversalDateClampingTests.cs`,
  `ReversalClosedYearTests.cs`, `JournalEntryStatusTests.cs`, etc.) are untouched by
  this refactor and must keep passing — no service-layer code changes.
- `dotnet build` / `dotnet test` for a basic regression check, same as prior PRs in
  this area.

## Branch / PR strategy

Branches from `dialog-actions-component` (#215, draft, not yet merged), not from
`journal-entry-storno-compliance`/main. `DialogActions.razor` (the shared
right-aligned button-row wrapper added in #215) needs no changes — it only wraps
`ChildContent` and is agnostic to how its parent dialog is opened — but #215 touches
the same three files inside `DialogContent`, so stacking avoids a guaranteed later
merge conflict for no benefit. The PR for this work should target `dialog-actions-component`
as its base (`gh pr create --base dialog-actions-component`) rather than main; once
#215 merges, GitHub retargets this PR to main automatically. If #215 picks up new
commits before merging, this branch needs a `git rebase` to follow them.

## Out of scope

- No change to `MudDialogProvider`'s registration in `App.razor` — already present, unused until now.
- No change to any service-layer code (`JournalEntryService`, `DocumentService`, etc.) — this is purely a hosting-mechanism swap for the three dialog components and their two parent pages.
- No new automated test coverage beyond what's listed above — matches existing convention for this codebase's dialogs.
