# Stream Document Uploads — Design Spec

**Date:** 2026-07-09
**Status:** Approved
**Issue:** [#187](https://github.com/flojon/koalabooks/issues/187)

## Overview

Remove the redundant `byte[]`/`MemoryStream` buffering that today's single-file upload path builds on top of the browser's own upload stream, before the bytes ever reach `DocumentService`. `IDocumentStorage.SaveAsync` and `DocumentService.UploadAsync`/`UploadAndLinkAsync` move from `byte[] data` to `Stream data`; all four Razor upload call sites (`Inbox.razor`, `CustomerInvoices.razor`, `SupplierInvoices.razor`, `Journal.razor`) pass `IBrowserFile.OpenReadStream()` straight through instead of copying it into a `MemoryStream` and calling `.ToArray()` first.

## Goals / Non-Goals

- **Goal:** eliminate the extra in-memory copies (`MemoryStream` + `ToArray()`) that each Razor upload handler builds today before calling into `DocumentService`, across all four upload call sites.
- **Goal:** land the `Stream`-shaped interface on `IDocumentStorage`/`DocumentService` that a future genuine-streaming storage backend can build on.
- **Non-goal:** true end-to-end streaming to Postgres. `DbDocumentStorage` still materializes the full file into a `byte[]` internally to write the existing `bytea` column — Npgsql has no streaming write API for `bytea`. Removing that last buffer requires migrating to Postgres Large Objects (`NpgsqlLargeObjectManager`), which needs a schema migration (`bytea` → `oid`) and transaction-scoped chunked I/O. Tracked as a new follow-up issue, kept separate from this one.
- **Non-goal:** streaming PDF text extraction. PdfPig (and every alternative checked — iText7, poppler) requires a seekable, fully-materialized file because the PDF trailer/xref table sits at end-of-file; this is a format constraint, not a library limitation. Extraction still runs against a `byte[]` read from the upload. Decoupling extraction into a background step that reads back a seekable copy after upload is tracked separately in [#208](https://github.com/flojon/koalabooks/issues/208)/[#209](https://github.com/flojon/koalabooks/issues/209).
- **Non-goal:** `UploadZipAsync` internals. It requires a seekable `ZipArchive` and keeps reading zip entries into `byte[]` as it does today (see [zip-inbox-upload-design.md](./2026-07-02-zip-inbox-upload-design.md)); it adapts to the new `UploadAsync` signature by wrapping each entry's `byte[]` in a `MemoryStream` when calling it.

## `IDocumentStorage`

Only `SaveAsync` changes; `LoadAsync`/`DeleteAsync` are untouched (download/delete aren't part of this issue):

```csharp
public interface IDocumentStorage
{
    Task<string> SaveAsync(int documentId, string contentType, Stream data);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
```

`DbDocumentStorage.SaveAsync` reads the incoming `Stream` into a `byte[]` via `CopyToAsync(memoryStream)` and writes it to `DocumentData.Data` exactly as today — this is the acknowledged remaining buffer, addressed by the Large Objects follow-up, not this issue.

## `DocumentService`

`UploadAsync` and `UploadAndLinkAsync` change their `byte[] data` parameter to `Stream data`. A `Stream` doesn't reliably expose `Length` up front and a caller-declared size shouldn't be trusted, so both read into a bounded buffer capped at `MaxBytes` (10MB) via a shared helper:

```csharp
private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes)
{
    using var ms = new MemoryStream();
    var buffer = new byte[81920];
    long total = 0;
    int read;
    while ((read = await stream.ReadAsync(buffer)) > 0)
    {
        total += read;
        if (total > maxBytes) return null;
        await ms.WriteAsync(buffer.AsMemory(0, read));
    }
    return ms.ToArray();
}
```

(This generalizes the bounded-read logic `UploadZipAsync` already uses per zip entry into a shared helper, rather than having two separate implementations of "read up to N bytes and reject if more.")

```csharp
public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Stream data)
{
    if (currentUser.OrganisationId is null) return (null, "Ingen aktiv organisation.");
    if (!AllowedContentTypes.Contains(contentType)) return (null, "Otillåten filtyp. Tillåtna typer: PDF, PNG, JPEG.");

    var bytes = await ReadBoundedAsync(data, MaxBytes);
    if (bytes is null) return (null, "Filen är för stor (max 10 MB).");

    // unchanged from here: create Document row, storage.SaveAsync(doc.Id, contentType, new MemoryStream(bytes)),
    // extractor.ExtractAsync(fileName, contentType, bytes)
}
```

`storage.SaveAsync` is called with `new MemoryStream(bytes)` wrapping the same array — zero extra copy at that point. Extraction still runs against the materialized `bytes` array, unchanged.

`UploadZipAsync` keeps producing a `byte[]` per entry (already bounded via the same shared helper) and wraps it in a `MemoryStream` when calling the now-`Stream`-based `UploadAsync`.

## Razor call sites

All four handlers change identically — drop the `MemoryStream`/`CopyToAsync`/`ToArray()` sequence and pass the browser stream straight through:

```csharp
// before (all four files, same shape):
using var ms = new MemoryStream();
await file.OpenReadStream(maxBytes).CopyToAsync(ms);
await DocumentService.UploadAsync(file.Name, contentType, ms.ToArray());

// after:
await using var stream = file.OpenReadStream(maxBytes);
await DocumentService.UploadAsync(file.Name, contentType, stream);
```

- `Inbox.razor` — single-file branch changes as above; the `.zip` branch is untouched (still buffers via `UploadZipAsync`'s existing path).
- `CustomerInvoices.razor`, `SupplierInvoices.razor`, `Journal.razor` — each calls `UploadAndLinkAsync` with the same pattern; same conversion, three lines removed per file instead of four (no `ToArray()` step to remove separately since the `MemoryStream` step is removed entirely).
- Size-limit checks by extension (`maxBytes`/`zipMaxBytes`) are unchanged.

## Error handling

- `ReadBoundedAsync` returning `null` produces the existing friendly Swedish error (`"Filen är för stor (max 10 MB)."`), same message and behavior as today's `data.Length > MaxBytes` check — no new exception types reach the UI.
- Streams are disposed via `await using` at each Razor call site.
- If `OpenReadStream` itself throws (e.g. the browser mis-reports file size), it propagates the same as it does today — unchanged.

## Testing

- `DocumentServiceTests`: `UploadAsync`/`UploadAndLinkAsync` against a mock `IDocumentStorage`, covering normal upload and oversized-stream rejection via `ReadBoundedAsync`. Existing `UploadZipAsync` tests continue to pass unchanged (adapted only at the call site into `UploadAsync`).
- `DbDocumentStorage` test asserting a `Stream` argument round-trips correctly into `DocumentData.Data`.
- Manual verification: drive the actual upload flow through the running app (`Inbox.razor` single-file upload, and one of the link flows e.g. `SupplierInvoices.razor`) in a browser, not just unit tests.

## What Is Not Changing

- `IDocumentStorage.LoadAsync`/`DeleteAsync` — unchanged.
- `DbDocumentStorage`'s underlying `bytea` column / EF Core mapping — unchanged (Large Objects migration is a separate follow-up issue).
- `IDocumentExtractor` / `PdfTextExtractor` — unchanged, still `byte[]`-based (tracked separately in #208/#209).
- `UploadZipAsync`'s internal zip-entry handling — unchanged, only its call into `UploadAsync` adapts to the new signature.
- Per-file/per-zip size and type limits — unchanged.
