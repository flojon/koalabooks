# Storno preview popup (#174 follow-up)

## Problem

The Journal page's reversal ("Återför") action, added in #174, is a tiny
inline row (a reason textbox plus Bekräfta/Avbryt buttons) that appears in
place of the row-action menu. It gives no indication of what the reversal
will actually do — which entry number it will get, what date it will be
posted on, or which lines will be mirrored. For a compliance-driven feature
(BFL 5:7 / BFNAR 2013:2 storno), the user should be able to see exactly
what will be created before confirming.

## Goal

Replace the inline reason-input row with a modal popup that previews the
reversal entry before it's created — original entry on top, generated
reversal on the bottom — computed via a shared code path so the preview
can never drift from what actually gets persisted.

## Service layer changes (`JournalEntryService`)

`CreateReversalAsync` currently does validation, building, and persistence
in one method. Split it:

- **`BuildReversal(JournalEntry original, string reason, int nextEntryNumber, FiscalYear fiscalYear)`**
  (private) — pure construction: mirrors each line (debit↔credit swap),
  clamps the date to the fiscal year, formats the description as
  `Reversal of #{original.EntryNumber}: {reason}`, sets
  `Status = Correction`, `SourceJournalEntryId = original.Id`. Returns an
  unsaved `JournalEntry`. No DB calls inside this method — the caller
  supplies `nextEntryNumber` and `fiscalYear`.
- **Shared validation** (private, e.g. `ValidateReversal(JournalEntry original)`
  returning `string? Error`) — the existing guard checks: entry exists,
  `IsPosted`, not already `Reversed`, fiscal year not closed. Reused by
  both paths below so an error surfaces identically whether previewing or
  confirming.
- **`CreateReversalAsync(int entryId, string reason)`** — unchanged
  signature/behavior. Now: load original → validate → compute
  `nextEntryNumber` (existing `MaxAsync` query) → `BuildReversal` → mark
  original `Reversed` → save → `PropagateAffectedAccountsAsync` (all
  unchanged).
- **New `PreviewReversalAsync(int entryId, string reason)`** — load
  original → validate → compute `nextEntryNumber` → `BuildReversal` →
  return `(JournalEntry? Preview, string? Error)` **without** touching
  `_db.JournalEntries.Add`, calling `SaveChangesAsync`, or setting
  `original.Status = Reversed`. That status flip must stay exclusive to
  `CreateReversalAsync` — setting it on the EF-tracked `original` entity
  during a preview would mark it dirty on the scoped `DbContext` even
  without a `SaveChangesAsync` call, and a later unrelated save on the
  same scope (e.g. another action during the same Blazor circuit) could
  persist it prematurely. Since `nextEntryNumber` is a `MAX(EntryNumber)`
  read, calling this repeatedly (once per dialog open) is safe — it never
  reserves a number, so the previewed number is provisional and could
  differ if another entry is created concurrently. This mirrors the
  existing race window already present in `CreateReversalAsync` itself
  (unchanged, not introduced by this feature).

## New component: `Shared/ReversalPreviewDialog.razor`

Follows the existing plain-`<MudDialog>` convention used by
`ClassifyDocumentDialog.razor` / `PreviewDocumentDialog.razor` — no
`DialogService`/`MudDialogProvider`, just conditionally rendered by the
parent page.

- **Parameters:** `Entry` (`JournalEntry`, the original — already loaded
  with `Lines`/`Account` by the parent's existing query),
  `OnReversed` (`EventCallback`), `OnClose` (`EventCallback`).
- **State:** `_reason` (string, starts `""`), `_preview` (`JournalEntry?`),
  `_loadError` (string?, preview-fetch failure), `_submitError` (string?,
  confirm failure), `_loading` (bool), `_saving` (bool).
- **`OnInitializedAsync`**: calls
  `JournalEntryService.PreviewReversalAsync(Entry.Id, "")` to get the
  provisional entry number, date, and mirrored lines. These don't depend
  on the reason text, so one call is enough.
- **Reason field:** required. Confirm button `disabled` while
  `string.IsNullOrWhiteSpace(_reason)`. The description line shown for the
  reversal preview is computed client-side as
  `$"Reversal of #{Entry.EntryNumber}: {_reason}"` for live feedback as the
  user types (plain `@bind`, so it updates on blur/Enter like the rest of
  the codebase's text inputs) — this is a one-line format string, low risk
  of drifting from the service's copy, unlike the amount/date logic which
  stays server-side only.
- **Body layout**, top to bottom:
  1. If `_loadError` is set: show it in a `MudAlert Severity="Error"` and
     only a "Stäng" button — no preview tables rendered.
  2. Otherwise, two stacked cards using the same table shape as the
     Journal page (`Konto | Debet | Kredit` columns):
     - **Original** — `#{Entry.EntryNumber}`, `Entry.Date`,
       `Entry.Description`, its lines.
     - ↓ visual divider (e.g. "återförs som" / "will be reversed as").
     - **Reversal (preview)** — `#{_preview.EntryNumber}` (labeled
       "preliminärt" since it's provisional per above),
       `_preview.Date`, the live description, mirrored lines.
  3. Reason `<input>` (required, as above).
  4. `_submitError`, if set, as a `MudAlert Severity="Error"`.
  5. Bekräfta (disabled while `_loading || _saving ||
     string.IsNullOrWhiteSpace(_reason)`) / Avbryt buttons.
- **Confirm**: calls
  `JournalEntryService.CreateReversalAsync(Entry.Id, _reason)` directly
  (matching `ClassifyDocumentDialog`'s pattern of calling services itself
  rather than delegating to the parent). On error, sets `_submitError` and
  stays open. On success, invokes `OnReversed`.
- **Dialog width**: single-column stacked content, narrower than the
  900px two-pane dialogs — `max-width:640px; width:95vw;`.

## `Journal.razor` changes

- Remove: `_reversingEntryId`, `_reversalReason`, `StartReversal(int)`,
  `CancelReversal`, `ConfirmReversal(int)`, and the inline
  `@if (_reversingEntryId == entry.Id)` block (current lines 126–133).
- Add: `_reversingEntry` (`JournalEntry?`), `StartReversal(JournalEntry entry)`
  setting it, and `<ReversalPreviewDialog>` rendered once outside the
  `<table>` (same placement convention as `_classifyDoc` in `Inbox.razor`),
  with `OnClose="() => _reversingEntry = null"` and
  `OnReversed="async () => { _reversingEntry = null; await ReloadEntriesAsync(); Snackbar.Add(...); }"`.
- The `MudMenuItem` "Återför" changes from
  `OnClick="() => StartReversal(entry.Id)"` to
  `OnClick="() => StartReversal(entry)"`.
- The now-unused row-action-column special case for
  `_reversingEntryId == entry.Id` is removed; the row falls through to the
  normal `MudMenu` rendering at all times (the dialog, not the row,
  now owns the "in progress" state).

## Error handling

- **Preview-fetch failure** (e.g. entry was reversed by someone else, or
  the fiscal year closed, between opening the row menu and the dialog
  finishing its load): shown as `_loadError` inside the dialog, no stale
  or misleading preview rendered, user can only close.
- **Confirm failure** (same guards re-checked inside `CreateReversalAsync`,
  covering the race between preview and submit): shown as `_submitError`
  inline, dialog stays open so the user isn't forced to redo the reason
  text after a transient failure.

## Testing

- New service-level tests for `PreviewReversalAsync`, mirroring the
  existing `ReversalDateClampingTests.cs` / `ReversalClosedYearTests.cs`
  structure:
  - Returns the same mirrored lines/entry number/date shape a subsequent
    `CreateReversalAsync` call would produce.
  - Does not persist anything (`_db.JournalEntries` count unchanged,
    original entry's `Status` unchanged after calling preview).
  - Fails with the same errors as `CreateReversalAsync` for: entry not
    posted, already reversed, fiscal year closed.
- Existing `CreateReversalAsync`-focused tests
  (`ReversalDateClampingTests.cs`, `ReversalClosedYearTests.cs`,
  `JournalEntryStatusTests.cs`, `AuditTrailTests.cs`,
  `JournalEntryDbGuardTests.cs`) must keep passing unchanged — the
  refactor is behavior-preserving for the existing public method.
- Manual browser verification (Playwright, matching #174's existing
  manual-check pattern): open the row menu → Återför → popup shows
  original + provisional reversal → typing a reason updates the live
  description → Confirm creates the reversal and closes the dialog →
  row updates to "Återförd" with no further actions available.

## Out of scope

- Making the reason field required is scoped to this dialog only; the
  REST API's `POST /api/v1/journal-entries/{id}/reverse` (added in #174)
  is unaffected — it already accepts `CreateReversalAsync`'s existing
  `reason` parameter as-is, and this change doesn't touch the API layer.
