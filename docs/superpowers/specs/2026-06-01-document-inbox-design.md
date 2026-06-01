# Document Inbox — Design Spec

**Date:** 2026-06-01  
**Status:** Approved

## Overview

A document inbox where users upload files (PDFs, images) that sit unprocessed until sorted into a supplier invoice, customer invoice, or journal entry. Basic non-AI extraction pre-fills fields from PDF text and filename heuristics. The `Document` entity becomes the single unified document store for all attachment use cases, replacing the existing `JournalEntryAttachment` table.

---

## Data Model

### New entity: `Document`

```csharp
public class Document
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }

    // Extraction hints (nullable — set after extraction runs)
    public string? SuggestedType { get; set; }   // "SupplierInvoice" | "CustomerInvoice" | "JournalEntry"
    public string? ExtractedDataJson { get; set; } // JSON: { supplier, amount, date, dueDate, invoiceNumber }

    // Navigation (many-to-many — EF join tables)
    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<SupplierInvoice> SupplierInvoices { get; set; } = [];
    public List<CustomerInvoice> CustomerInvoices { get; set; } = [];
}
```

File bytes are stored via `IDocumentStorage` (not as a column on `Document` directly — the storage implementation decides where bytes live).

### Storage abstraction

```csharp
public interface IDocumentStorage
{
    Task<string> SaveAsync(int documentId, string contentType, byte[] data);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
```

Initial implementation: `DbDocumentStorage` stores bytes in a `DocumentData` table (one row per document, same org-scoped query filter). Storage key = `documentId.ToString()`. This keeps the `Document` entity clean and makes future S3 migration a drop-in swap (see [#123](https://github.com/flojon/koalabooks/issues/123)).

### EF join tables (auto-created by EF Core many-to-many)

- `DocumentJournalEntries` (DocumentId, JournalEntryId)
- `DocumentSupplierInvoices` (DocumentId, SupplierInvoiceId)
- `DocumentCustomerInvoices` (DocumentId, CustomerInvoiceId)

### Inbox definition

A document is **pending** (shown in inbox) when it has no rows in any join table. Queried as:

```csharp
db.Documents
  .Where(d => !d.JournalEntries.Any()
           && !d.SupplierInvoices.Any()
           && !d.CustomerInvoices.Any())
```

A document can link to multiple entities — e.g. a supplier invoice PDF automatically gains a second link to the journal entry created when the invoice is posted.

### Migration

One EF migration:
1. Create `Document`, `DocumentData`, and three join tables
2. Copy rows from `JournalEntryAttachment` → `Document` + `DocumentData` + `DocumentJournalEntries`
3. Drop `JournalEntryAttachment`

---

## Extraction Pipeline

Runs immediately after upload. Returns `ExtractionResult` with nullable fields; UI shows them as pre-filled suggestions the user can edit.

```csharp
public record ExtractionResult(
    string? SuggestedType,
    string? Supplier,
    decimal? Amount,
    decimal? VatAmount,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string? InvoiceNumber
);

public interface IDocumentExtractor
{
    Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data);
}
```

Two implementations composed by `CompositeExtractor`:

**`FilenameExtractor`** — Swedish keyword heuristics on the filename (case-insensitive):
- "faktura" / "invoice" / "fakt" → `SupplierInvoice`
- "kvitto" / "receipt" → `JournalEntry`
- "kundfaktura" / "customer" → `CustomerInvoice`

**`PdfTextExtractor`** — only runs for `application/pdf`. Uses **PdfPig** to extract the text layer (no OCR). Regex patterns for Swedish invoice fields:
- Amount: `([\d\s]+[,.][\d]{2})\s*(kr|SEK)`
- Invoice number: `[Ff]akturanummer[:\s]+(\S+)` / `[Ii]nvoice\s+[Nn]o[:\s]+(\S+)`
- Invoice date: `[Ff]akturadatum[:\s]+(\d{4}-\d{2}-\d{2})`
- Due date: `[Ff]örfallodatum[:\s]+(\d{4}-\d{2}-\d{2})`
- Supplier: heuristic — first line of text that looks like a company name (ends in AB, KB, HB, AB, Inc, etc.)

`CompositeExtractor` runs both, merges results (PdfTextExtractor fields take priority over FilenameExtractor).

Image files (JPEG, PNG, etc.) get filename extraction only — no OCR.

File size limit: **10 MB** (consistent with existing journal attachment limit).

---

## Services

### `DocumentService` (Application layer)

Replaces `AttachmentService`.

```
UploadAsync(fileName, contentType, data) → Document
  - Validates file size
  - Saves via IDocumentStorage
  - Runs IDocumentExtractor, stores result on Document
  - Returns Document (with extraction hints)

GetPendingAsync() → List<DocumentMeta>
  - Documents with no join-table rows (the inbox)

GetLinkedAsync(entityType, entityId) → List<DocumentMeta>
  - All documents linked to a specific entity

GetDownloadAsync(documentId) → (contentType, byte[])
  - Loads bytes via IDocumentStorage

DeleteAsync(documentId) → bool
  - Deletes from storage + DB

LinkAsync(documentId, entityType, entityId)
  - Adds a row to the appropriate join table

ClassifyAsSupplierInvoiceAsync(documentId, invoiceData) → (SupplierInvoice, error)
  - Creates SupplierInvoice, calls LinkAsync

ClassifyAsCustomerInvoiceAsync(documentId, invoiceData) → (CustomerInvoice, error)
  - Creates CustomerInvoice, calls LinkAsync

ClassifyAsJournalEntryAsync(documentId, entryData) → (JournalEntry, error)
  - Creates JournalEntry + Lines, calls LinkAsync

LinkToExistingJournalEntryAsync(documentId, entryId) → error?
  - Validates entry exists, calls LinkAsync
```

Auto-link on invoice post: `SupplierInvoiceService.PostAsync` and `CustomerInvoiceService.PostAsync` call `DocumentService.LinkAsync` on any documents already linked to the invoice, adding the new journal entry link.

### `IDocumentProvider` (replaces `IAttachmentProvider`)

```csharp
public interface IDocumentProvider
{
    string GetDownloadUrl(int documentId);
}
```

`WebDocumentProvider` implementation generates the download URL (same pattern as existing `WebAttachmentProvider`).

---

## UI

### `/inkorg` page (`Inkorg.razor`)

- Page header + "Ladda upp" button (and drag-and-drop zone)
- Upload accepts PDF and image files (max 10 MB)
- Calls `DocumentService.UploadAsync`, shows extraction suggestion immediately
- Table of pending documents: filename, size, upload time, suggested type badge, "Klassificera" button
- "Klassificera" opens `ClassifyDocumentDialog`

### `ClassifyDocumentDialog` (MudDialog, ~900px wide)

50/50 split layout:

**Left panel — document preview**
- PDF: `<iframe>` with the document download URL (browser renders PDF natively)
- Image: `<img>` tag
- Filename + file size shown below

**Right panel — classification form**
- Type selector (MudSelect): Leverantörsfaktura / Kundfaktura / Verifikation
- Pre-filled from `ExtractionResult` (editable)
- Form fields change based on selected type:

  *Leverantörsfaktura:* Leverantör*, Fakturanummer, Fakturadatum*, Förfallodatum*, Belopp exkl. moms*, Momsbelopp

  *Kundfaktura:* Kund (dropdown from existing customers)*, Fakturanummer, Fakturadatum*, Förfallodatum*, Belopp exkl. moms*, Momsbelopp

  *Verifikation:* Toggle "Ny verifikation" / "Koppla till befintlig"
  - Ny: Datum*, Beskrivning*, debit/credit rows (same component as Journal.razor)
  - Befintlig: journal entry search dropdown

- "Skapa & koppla" button — calls the appropriate `DocumentService.ClassifyAs*` method, closes dialog, removes document from inbox list

### Modified pages

**`Journal.razor`** — swap `AttachmentService`/`JournalEntryAttachment` for `DocumentService`. Upload, list, download, delete all go through `DocumentService`. Behaviour unchanged for the user.

**`SupplierInvoices.razor`** — add a document panel in the expanded row (same style as the journal attachment panel). Shows documents linked to the invoice via `DocumentService.GetLinkedAsync(SupplierInvoice, id)`. Upload button attaches directly (skips inbox classification).

**`CustomerInvoices.razor`** — same document panel as supplier invoices.

---

## Error Handling

- File too large (> 10 MB): show inline error, do not upload
- Unsupported content type: warn but allow upload (extraction skipped)
- PDF extraction failure: log warning, continue with filename-only extraction — never block the upload
- Classification failure (service returns error string): show in dialog, keep dialog open
- Storage failure: surface as user-visible error with retry option

---

## Testing

- `DocumentExtractorTests` — filename heuristics, PDF regex patterns against sample text strings
- `DocumentServiceTests` — upload, link, classify flows using the existing `TestFixture` (real Postgres container)
- `InboxComponentTests` — not planned for initial build; manual testing of the dialog
