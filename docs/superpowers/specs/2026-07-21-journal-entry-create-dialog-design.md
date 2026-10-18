# Journal entry creation as a popup dialog, with attach-on-create (#197)

## Problem

The "Ny verifikation" form on `Journal.razor` is an inline card
(`_showForm`, lines 50–68) pushed into the page flow above the entries
table, rather than a focused popup. It also has no way to attach a
document while creating an entry — attaching is only possible after the
entry is saved/posted (via the 📎 panel, further down the same page), or
by going through the Inbox's "create new + link" classify flow
(`ClassifyDocumentDialog.ClassifyAsJournalEntryAsync`).

## Goal

Replace the inline card with a `NewJournalEntryDialog` popup, following the
codebase's established `IDialogService`/`MudDialogProvider` convention
(see `ReversalPreviewDialog.razor`, `ClassifyDocumentDialog.razor`), and
let the user stage one or more files to attach as part of creating the
entry — uploaded and linked via `DocumentService.UploadAndLinkAsync(...,
DocumentEntityType.JournalEntry, entryId)` right after
`JournalEntryService.CreateAsync` returns the new entry's `Id`.

## New component: `Shared/NewJournalEntryDialog.razor`

Follows `ReversalPreviewDialog.razor`'s structure exactly (plain
`<MudDialog>` root, `[CascadingParameter] IMudDialogInstance MudDialog`,
no `Visible`/`@ref`).

- **Parameters:** `Accounts` (`List<Account>`, `[EditorRequired]`) —
  supplied by the caller from `Journal.razor`'s existing `_accounts`
  field, same as today's inline usage.
- **State:** `_date` (`DateTime`, starts `DateTime.Today`), `_description`
  (`string`, starts `""`), `_lines` (`List<JournalEntryForm.LineModel>`,
  starts `[new(), new()]`), `_isBalanced` (`bool`), `_isDirty` (`bool`,
  mirroring `ClassifyDocumentDialog`'s self-contained dirty tracking — see
  "Unsaved-changes guard" below), `_pendingFiles` (`List<IBrowserFile>`,
  starts empty), `_error` (`string?`), `_saving` (`bool`).
- **Body**, top to bottom:
  1. `<UnsavedChangesGuard IsDirty="_isDirty" />`, exactly as
     `ClassifyDocumentDialog.razor:8` does — self-contained inside the
     dialog, not threaded to the host page (see "Unsaved-changes guard"
     below).
  2. The existing `<JournalEntryForm>` component, unchanged — same
     `Accounts`/`Date`/`DateChanged`/`Description`/`DescriptionChanged`/
     `Lines`/`IsBalancedChanged` wiring as today's inline usage, plus
     `DirtyChanged="MarkDirty"` where `private void MarkDirty() => _isDirty
     = true;` (identical to `ClassifyDocumentDialog.razor:239`).
  3. A `MudFileUpload<IReadOnlyList<IBrowserFile>>` (mirroring
     `Inbox.razor`'s upload control — `CustomContent` with a
     `MudButton`/`Icons.Material.Filled.AttachFile`, no drag-and-drop
     `SelectedTemplate` needed since it only appends to `_pendingFiles`,
     doesn't upload immediately: `FilesChanged="files =>
     _pendingFiles.AddRange(files)"`). No `Accept`/`MaximumFileCount`
     restriction beyond the existing per-file 10&nbsp;MB check applied at
     upload time (matching `UploadAttachmentAsync`'s limit, not the
     Inbox's stricter type/count limits — this is a general attachment,
     not an inbox intake).
  4. A simple chip list under the upload button, one row per
     `_pendingFiles` entry (`file.Name` + a small "✕" remove button that
     splices it out of the list before submit).
  5. `_error`, if set, as `MudAlert Severity="Error"`.
  6. `DialogActions`: "💾 Bokför" (`SaveAndPostAsync`), "Spara som utkast"
     (`SaveAsDraftAsync`), "Avbryt" (`MudDialog.Cancel`, `disabled="@_saving"`)
     — same three actions/labels as today's inline buttons, both save
     buttons `disabled="@(!_isBalanced || _saving)"`.
- **`SaveAndPostAsync`/`SaveAsDraftAsync`** call a shared private
  `SaveAsync(bool post)`, mirroring `Journal.razor`'s current
  `SaveAndPost`/`SaveAsDraft`/`SaveEntryAsync(bool)` split:
  1. Guard re-entrancy (`if (_saving) return;`), set `_saving = true`.
  2. Build the `JournalEntry` from `_date`/`_description`/`_lines`
     exactly as today's `SaveEntryAsync` does (`FiscalYearId` now comes
     from a new `[Parameter, EditorRequired] public int FiscalYearId`,
     since the dialog doesn't have direct access to the parent's
     `_activeFiscalYear`).
  3. Call `JournalEntryService.CreateAsync(entry)`. On error, set
     `_error`, stay open, `return` (nothing attached yet — matches
     today's error path).
  4. If `post`, call `JournalEntryService.PostAsync(result!.Id)`. On
     error, set `_error`, stay open, `return` — same as today
     (`SaveEntryAsync`'s comment about create/post not being wrapped in a
     transaction still applies unchanged).
  5. For each file in `_pendingFiles`, call
     `DocumentService.UploadAndLinkAsync(file.Name, contentType,
     () => file.OpenReadStream(maxBytes), DocumentEntityType.JournalEntry,
     result!.Id)`, using the same 10&nbsp;MB `maxBytes` limit and
     content-type fallback (`string.IsNullOrWhiteSpace(file.ContentType) ?
     "application/octet-stream" : file.ContentType`) as
     `UploadAttachmentAsync`. Collect failures into a
     `List<string> _failedFiles` (file name only) — **don't** abort the
     loop or block on a single failure.
  6. Regardless of attachment outcome, call
     `MudDialog.Close(DialogResult.Ok(new NewEntryResult(result!, post,
     _failedFiles)))` — a small result record (`JournalEntry Entry, bool
     Posted, List<string> FailedFiles`) so the caller can build the right
     snackbar message(s) without a second round trip.
  7. `finally { _saving = false; }` (won't visibly run after `Close`, but
     matches `ReversalPreviewDialog`'s try/finally shape for the error
     path).

## `Journal.razor` changes

- Remove: `_showForm`, `_formDate`, `_formDescription`, `_formLines`,
  `_isBalanced`, `_isDirty`, `MarkDirty`, `NewEntry`, `CancelForm`,
  `SaveAndPost`, `SaveAsDraft`, `SaveEntryAsync`, the `@if (_showForm)`
  card block (lines 50–68), and the `<UnsavedChangesGuard IsDirty="_isDirty"
  />` line (27).
- `+ Ny verifikation` button's `@onclick` changes from `NewEntry` to a new
  `OpenNewEntryDialogAsync`:

```csharp
private async Task OpenNewEntryDialogAsync()
{
    var parameters = new DialogParameters<NewJournalEntryDialog>
    {
        { x => x.Accounts, _accounts },
        { x => x.FiscalYearId, _activeFiscalYear!.Id }
    };
    var dialogRef = await DialogService.ShowAsync<NewJournalEntryDialog>(
        "Ny verifikation", parameters, DialogDefaults.NoDismiss);
    var result = await dialogRef.Result;
    if (result is { Canceled: false } && result.Data is NewJournalEntryDialog.NewEntryResult r)
    {
        Snackbar.Add(r.Posted
            ? $"Verifikation #{r.Entry.EntryNumber} bokförd."
            : $"Verifikation #{r.Entry.EntryNumber} sparad som utkast.",
            Severity.Success);
        if (r.FailedFiles.Count > 0)
        {
            Snackbar.Add($"Kunde inte bifoga: {string.Join(", ", r.FailedFiles)}. Försök igen via bilaga-panelen.",
                Severity.Warning);
        }
        await ReloadEntriesAsync();
    }
}
```

- The empty-state hint text (line 303, "Klicka 'Ny verifikation' för att
  skapa en.") and the "Inga verifikationer ännu" guard's `!_showForm`
  check (line 294) both drop the now-gone `_showForm` reference — the
  guard becomes just `!FilteredEntries.Any()`.

### Unsaved-changes guard: moves into the dialog, doesn't drop

Today's `_isDirty`/`MarkDirty`/`<UnsavedChangesGuard>` on `Journal.razor`
exist only to warn before navigating away from a dirty inline new-entry
form (confirmed: no other field on the page sets `_isDirty`). That
page-level wiring is removed along with the rest of `_showForm`'s state —
but the protection itself isn't dropped, it moves inside the dialog.

`ClassifyDocumentDialog.razor:8` already establishes the pattern for this
exact situation (a dialog holding in-progress, non-autosaved user input):
a **self-contained** `<UnsavedChangesGuard IsDirty="_isDirty" />`, with its
own local `_isDirty`/`MarkDirty` driven by its form fields' `:after`/
`DirtyChanged` callbacks — not threaded through to the host page at all.
`NewJournalEntryDialog` follows that same pattern: `JournalEntryForm`
already exposes a `DirtyChanged` callback for exactly this purpose
(`JournalEntryForm.razor:111`, fired on every date/description/line edit),
so wiring `DirtyChanged="MarkDirty"` and adding the guard is a few lines,
not a bespoke bridge. Without it, a user mid-entry (possibly with staged
attachments) who clicks a nav link or closes the tab would silently lose
their input — a regression from today's inline form that the
`ClassifyDocumentDialog` precedent shows isn't necessary.
`DialogDefaults.NoDismiss` continues to separately block backdrop-click/
Escape loss, same as today.

## Error handling

- **`CreateAsync`/`PostAsync` failure:** dialog stays open, `_error`
  shown inline (`MudAlert Severity="Error"`) — identical to today's
  behavior, since nothing exists yet to attach.
- **Partial attachment failure** (entry created/posted successfully, one
  or more `UploadAndLinkAsync` calls fail): dialog still closes — the
  entry is already committed and shouldn't be held hostage by an
  unrelated upload problem. The parent shows a second, `Warning`-severity
  snackbar naming the failed file(s) and pointing at the 📎 panel for
  retry (that panel's existing `UploadAttachmentAsync` is unchanged and
  already supports adding attachments to an existing posted/draft entry).

## Testing

`#210`'s design doc (2026-07-11) noted no bUnit/component-level tests
existed for any dialog — that's now stale: `PreviewDocumentDialogTests.cs`
and `UnsavedChangesConfirmDialogTests.cs` (added 2026-07-20) render dialogs
directly via a `MudDialogProvider`-hosted bUnit context and already cover
`ClassifyDocumentDialog`. `NewJournalEntryDialog` gets a bUnit suite
following that same pattern, plus manual Playwright verification for the
end-to-end upload/attachment flow (bUnit can't easily drive real
`IBrowserFile` uploads).

**bUnit** (`tests/KoalaBooks.ComponentTests/`, mirroring
`PreviewDocumentDialogTests.cs`'s harness):

- Renders with `Accounts`/`FiscalYearId` supplied — dialog shows
  `JournalEntryForm` with two empty lines, both save buttons disabled
  until balanced.
- Editing a line (or date/description) marks the dialog dirty; confirm
  this is observable the same way `ClassifyDocumentDialog`'s tests
  exercise its own `_isDirty` (e.g. via `UnsavedChangesGuard`'s
  `NavigationLock.ConfirmExternalNavigation` binding, or a lower-level
  check on the dialog's dirty state if the guard itself isn't easily
  assertable in bUnit).
- `CreateAsync` failure (stub `IJournalEntryService`): dialog stays open,
  `_error` renders as `MudAlert Severity="Error"`.
- `CreateAsync`/`PostAsync` success with stubbed `DocumentService`:
  dialog closes with `DialogResult.Ok` carrying a `NewEntryResult` whose
  `Entry`/`Posted`/`FailedFiles` match the stubbed outcomes (including the
  partial-failure case, `FailedFiles` non-empty but dialog still closes).

**Manual Playwright** (unchanged from the original plan, still needed for
what bUnit can't cover):

- Open dialog via "+ Ny verifikation", create an entry with zero
  attachments (both Bokför and Spara som utkast paths) — confirm identical
  behavior to today's inline form (snackbar text, list reload).
- Create an entry with one and with multiple staged files — confirm all
  are linked (verify via the 📎 panel's count/list on the resulting row).
- Remove a staged file via its ✕ before submitting — confirm it's not
  uploaded.
- Force an attachment failure (e.g. a >10 MB file) alongside a valid one
  — confirm the entry is still created, the valid file is linked, the
  warning snackbar names only the failed file, and the failed file is
  retryable via the 📎 panel afterward.
- Force a `CreateAsync` validation failure (e.g. unbalanced lines can't
  even reach submit since the buttons are disabled; use a scenario the
  service itself rejects, e.g. posting to a closed fiscal year if
  reachable) — confirm the dialog stays open with the inline error and
  no attachment upload was attempted.
- Confirm backdrop-click/Escape no longer dismiss the dialog
  (`DialogDefaults.NoDismiss`, matching the other two dialogs).

## Out of scope

- No change to the 📎 attachment panel's own upload path
  (`UploadAttachmentAsync`) — it remains the retry mechanism for
  partial-failure cases and for attaching to already-posted entries.
- No change to `ClassifyDocumentDialog`'s create-and-link flow — it's a
  separate entry point (classifying an already-uploaded Inbox document)
  and keeps using `LinkAsync` against a pre-existing `Document`.
- No change to `MudFileUpload`'s global size/type validation — reuses the
  10 MB-per-file check already enforced in `UploadAttachmentAsync`,
  applied client-side per file at submit time rather than by the control
  itself.
