# Inbox Preview, Sorting & Bookkeeping Date — Design Spec

**Date:** 2026-06-13
**Status:** Approved

## Overview

Three related improvements to the document inbox:

1. **Bug fix** — document preview blank because `/documents/{id}` serves with `Content-Disposition: attachment`
2. **Bookkeeping date** — new `DocumentDate` field on `Document`, auto-populated from extraction, manually editable
3. **Preview dialog** — lightweight view-and-tag dialog separate from the full classify flow
4. **Inbox table** — sorting, read-only type badge, bookkeeping date column, type dropdown removed

---

## Data Model

### `Document` entity

Add one nullable column:

```csharp
public DateOnly? DocumentDate { get; set; }
```

This is the bookkeeping date — the date relevant to the document itself (typically the invoice date), distinct from `UploadedAt`. Populated automatically from `ExtractionResult.InvoiceDate` on upload if extraction succeeds. Editable manually via the preview dialog.

### `DocumentMeta` projection

Add `DocumentDate` to `DocumentMeta` so the inbox table and preview dialog can read it without loading the full entity.

### `DocumentService`

- Replace `SetTypeAsync(id, type)` with `UpdateMetadataAsync(id, type, date)` — sets both `ClassifiedType` and `DocumentDate` in one call.
- In `UploadAsync`, after extraction: if `result.InvoiceDate.HasValue`, set `doc.DocumentDate = result.InvoiceDate`.
- `GetPendingAsync` gains a `sortBy` parameter (`"uploadedAt"` | `"documentDate"` | `"fileName"`, default `"uploadedAt"` descending). Sorting is server-side since results are paginated.

### Migration

One migration: add nullable `DocumentDate` (`date`) column to the `Documents` table.

---

## Bug Fix: Inline Document Serving

`/documents/{id:int}` currently calls `Results.File(data, contentType, fileName)`. Passing a filename sets `Content-Disposition: attachment`, causing browsers to download instead of render inline in an `<iframe>`.

**Fix:** drop the filename parameter:

```csharp
return Results.File(result.Value.Data, result.Value.ContentType);
// No filename → no Content-Disposition header → browser renders inline
```

If a download endpoint is needed in future, add `/documents/{id:int}/download` that includes the filename.

---

## Inbox Table

**Columns:** Filnamn · Storlek · Uppladdad · Bokföringsdatum · Typ · Åtgärder

- **Bokföringsdatum** — displays `DocumentDate` formatted as `yyyy-MM-dd`, dash if null. Sortable.
- **Typ** — read-only badge (e.g. "Leverantörsfaktura"). Empty if unclassified. The editable dropdown is removed.
- **Åtgärder** — three buttons: 👁 Preview · Klassificera · 🗑 Delete

Sortable column headers on Filnamn, Uppladdad, Bokföringsdatum. Clicking a header that is already the active sort toggles asc/desc. Active sort indicated visually (▲/▼). Sort state resets to page 1 on change.

Type filter chips at the top are unchanged.

---

## Preview Dialog (`PreviewDocumentDialog`)

A new Blazor component opened by the 👁 button. Identical outer shell to `ClassifyDocumentDialog` (MudDialog, 900px wide, 520px tall, two-pane flex).

### Left pane — document preview

- `<iframe>` for `application/pdf`, `<img>` for images
- `src` = `DocumentProvider.GetDownloadUrl(id)` — works correctly once the `Content-Disposition` bug is fixed
- Filename + size strip at bottom (same as classify dialog)

### Right pane — metadata form

| Field | Control | Source |
|-------|---------|--------|
| Typ | `<select>` | `ClassifiedType` |
| Bokföringsdatum | `<DateInput>` | `DocumentDate` (falls back to extracted `InvoiceDate` if `DocumentDate` is null) |
| Extracted summary | Read-only block | `ExtractedDataJson` (supplier, amount, invoice number) — shown only if non-null |

Buttons at the bottom:
- **Spara** — calls `UpdateMetadataAsync`, closes dialog, refreshes the row in the inbox table
- **Klassificera** — closes preview dialog and opens the existing `ClassifyDocumentDialog`
- **Avbryt** — closes without saving

### Data flow

1. User clicks 👁 → `_previewDoc` set → `PreviewDocumentDialog` renders
2. Dialog initialises: reads `ClassifiedType`, `DocumentDate`, deserialises `ExtractedDataJson`
3. User edits fields → clicks Spara → `UpdateMetadataAsync(id, type, date)` → dialog closes → `LoadPageAsync()` refreshes inbox
4. If user clicks Klassificera → `_previewDoc = null`, `_classifyDoc = doc` → classify dialog opens

---

## What Is Not Changing

- `ClassifyDocumentDialog` — unchanged
- Filter chips — unchanged
- Upload flow — unchanged (extraction already runs on upload)
- Pagination — unchanged
