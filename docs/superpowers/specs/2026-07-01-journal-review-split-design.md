# Journal / Review split (#170)

## Problem

The Journal page (`/journal`) currently shows draft and posted journal
entries mixed in one table, distinguished only by a status column. Drafts
are work-in-progress and clutter what should be a clean, authoritative
ledger of posted entries.

## Goal

Split `Journal.razor` into two pages:

- **Journal** (`/journal`) — posted entries only (`IsPosted == true`), a
  clean ledger view.
- **Review** (`/review`, nav label "Att granska") — draft entries only
  (`IsPosted == false`), with accept/edit/decline actions.

## Navigation

- New `MudNavLink` "Att granska" added to `MainLayout.razor`, in the
  Transaktioner group, next to the existing "Journal" link.
- Draft-count badge on the nav link, following the same pattern as the
  existing `_todoCount` badge (`MainLayout.razor` `LoadTodoCountAsync`):
  a dedicated DbContext scope query for `COUNT(*) WHERE IsPosted = false`
  in the active fiscal year, refreshed on navigation the same way
  `_todoCount` is.

## Journal page (`/journal`)

- `OnInitializedAsync` filters `_entries` to `IsPosted == true` (either via
  a new `JournalEntryService` query parameter, or client-side `.Where` —
  client-side is fine since the fiscal year's entries are already loaded
  in one query).
- Status column removed from the table (every row is posted, so it adds
  no information).
- Row actions column (`Åtgärder`) becomes a single `MudMenu` (⋮) per row
  with two possible items:
  - **Återför** — always available for a posted, non-closing entry. Opens
    the existing inline reason-input/confirm row, same as today.
  - **Skapa leverantörsfaktura** — shown only when `canConvert` (today's
    existing predicate: not a closing entry, not already linked, has a
    credit line to a `24*` account). Opens the existing inline
    create-supplier-invoice form, same as today's "Konvertera", just
    renamed and reframed as a create-and-link action (it never removes
    the journal entry — it links a `SupplierInvoice` to it via
    `SupplierInvoiceService.CreateFromEntryAsync`, which is unchanged).
  - The "📄 Faktura" linked indicator remains a plain badge next to the
    menu, not a menu item, since it's status, not an action.
- Create-entry form: unchanged fields (date, description, lines grid),
  but rendered via the new shared `JournalEntryForm` component (see
  below) instead of inline markup. Two submit buttons:
  - **Bokför** (primary) — calls `JournalEntryService.CreateAsync`, then
    `PostAsync(result.Id)`. New entry is posted and appears on Journal.
  - **Spara som utkast** (secondary) — calls `CreateAsync` only. New
    entry is a draft and appears on Review instead.
  - Both disabled unless the entry balances (unchanged `IsBalanced` rule).
- Attachments panel: unchanged.
- Draft-only actions (Redigera / Bokför / Radera as row buttons) are
  removed from Journal entirely — drafts never appear here.

## Review page (`/review`, new)

- New `Review.razor` page, same layout conventions as Journal (loading
  spinner, "no active fiscal year" alert, page title).
- Loads drafts for the active fiscal year: entries where
  `IsPosted == false`.
- Draft list rendered by a new `JournalReviewSection` component (see
  Extensibility below) with row actions as plain buttons (not a menu —
  only 2-3 actions, no need for a menu here):
  - **Redigera** — opens the shared `JournalEntryForm` inline in edit
    mode (same lines/date/description editing as today's Journal edit
    flow), saves via `JournalEntryService.UpdateAsync`.
  - **Acceptera** — calls `JournalEntryService.PostAsync(entry.Id)`.
    Functionally identical to today's "Bokför"; relabeled to fit the
    review framing.
  - **Avvisa** — calls `JournalEntryService.DeleteDraftAsync(entry.Id)`.
    Functionally identical to today's "Radera"; relabeled.
- No create-entry form on this page — creation stays on Journal (see
  above).

## Shared `JournalEntryForm` component

- New component under `KoalaBooks.Components/Shared/`, extracted from
  the markup and logic currently inline in `Journal.razor`'s `@code`
  block (date/description fields, lines grid with
  `AccountSearchDropdown`, add/remove line, Tab-to-add-line keyboard
  behavior, debit/credit totals, balance check).
- Used by:
  - Journal's create form (two submit buttons, as above).
  - Review's edit-in-place form (single "Spara" submit button, calling
    `UpdateAsync`).
- Parameters: initial date/description/lines (for edit mode), and
  callback(s) for submit action(s), so each page supplies its own
  button set and save logic while the component owns only the editing
  UI and balance validation.

## Out of scope (follow-up issue)

- Customer-invoice-from-entry: `CustomerInvoiceService` requires
  structured invoice line items (products/services, quantities, VAT
  rates) and posts via a wizard requiring explicit account mappings
  (`receivableAccountId`, `revenueAccountId`, per-VAT-rate accounts). A
  flat journal entry has no structured lines to map from, and no
  `CreateFromEntry`-equivalent exists for `CustomerInvoiceService` today.
  Needs its own design.
- "Convert draft journal entry → draft invoice" (supplier or customer):
  a different operation from today's link-while-posted flow — it would
  delete/replace the draft entry rather than just linking to it. Needs
  its own design, filed separately.

## Testing

- No new domain logic is introduced (all service calls reused as-is:
  `CreateAsync`, `PostAsync`, `UpdateAsync`, `DeleteDraftAsync`,
  `CreateFromEntryAsync`), so no new service-layer tests are expected
  beyond what `DraftFilteringTests.cs` / `DeleteDraftAsyncTests.cs` /
  `UpdateAsyncTests.cs` already cover.
- Manual verification: create a draft, confirm it appears only on
  Review and not Journal; accept it, confirm it moves to Journal;
  create-and-post directly from Journal, confirm it never appears on
  Review; edit a draft on Review; decline a draft; reverse a posted
  entry from the Journal row menu; create a supplier invoice from the
  Journal row menu.
