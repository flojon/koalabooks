# Zip-File Inbox Upload — Design Spec

**Date:** 2026-07-02
**Status:** Approved

## Overview

Let users bulk-import inbox documents by uploading a single `.zip` file through the existing document inbox picker, instead of selecting files one at a time. The zip is unpacked **server-side**; each entry runs through the exact same validation/storage/extraction pipeline a normal single-file upload already uses. No new UI element — `.zip` is simply added to the accepted extensions on the existing `MudFileUpload` control.

## Goals / Non-Goals

- **Goal:** bulk-import via zip using today's per-file rules (pdf/png/jpg, 10MB/file), with graceful partial success.
- **Non-goal:** folder-structure-aware classification (nested paths are flattened/ignored).
- **Non-goal:** streaming end-to-end. This feature keeps the existing `byte[]`-based buffering pattern; true streaming is tracked separately as [#187](https://github.com/flojon/koalabooks/issues/187) and is explicitly out of scope here — see [Memory and Streaming](#memory-and-streaming) below for why.

## `DocumentService.UploadZipAsync`

New method in `src/KoalaBooks.Application/Services/DocumentService.cs`, alongside the existing `UploadAsync`/`UploadAndLinkAsync`.

```csharp
public async Task<ZipImportResult> UploadZipAsync(byte[] zipData)
```

Behavior:

1. Validate the zip container itself: reject if `zipData.Length > ZipMaxBytes` (50MB) before opening it.
2. Open `zipData` with `System.IO.Compression.ZipArchive` (BCL — no new package).
3. Reject the whole zip if entry count (excluding directories) exceeds `ZipMaxEntries` (50).
4. Iterate entries **sequentially, one at a time**:
   - Skip directory entries (`entry.FullName.EndsWith('/')` or `entry.Length == 0 && entry.Name == ""`).
   - Flatten path: use `Path.GetFileName(entry.FullName)` only, nested folders ignored.
   - Check `entry.Length` (declared uncompressed size) against the existing per-file `MaxBytes` (10MB) **before** reading — reject oversized entries without allocating for them.
   - Infer content-type from file extension via a small map (`.pdf`→`application/pdf`, `.png`→`image/png`, `.jpg`/`.jpeg`→`image/jpeg`); unrecognized extensions are skipped without opening the entry stream.
   - Read the entry into a `byte[]`, call the existing `UploadAsync(name, contentType, data)` — reusing all current validation, storage, extraction, and tenant-scoping logic unchanged.
   - On success, add the resulting `Document` to `Imported`. On failure (validation rejection, storage failure, corrupt entry), add `(fileName, reason)` to `Skipped` and continue to the next entry — one bad entry never aborts the batch.
5. Return `ZipImportResult { Imported, Skipped }`.

```csharp
public record ZipImportResult(
    IReadOnlyList<Document> Imported,
    IReadOnlyList<(string FileName, string Reason)> Skipped);
```

### Limits

| Limit | Value | Rationale |
|---|---|---|
| Zip container size | 50MB | Separate, larger cap than the single-file 10MB limit, since a zip legitimately bundles several files |
| Max entries per zip | 50 | Bounds worst-case total work per upload |
| Per-entry size | 10MB (existing `MaxBytes`) | Unchanged from today's single-file rule |
| Allowed entry types | pdf, png, jpg/jpeg (existing `AllowedContentTypes`) | Unchanged from today's single-file rule |

### Memory and Streaming

This method stays on the existing `byte[]`-based pattern used throughout `DocumentService`/`IDocumentStorage` today — it does not introduce streaming. What it does do is avoid the naive "decompress everything up front" trap: entries are processed **one at a time** in the loop above, so peak memory is bounded to roughly *zip container (≤50MB) + one entry buffer (≤10MB)*, not all 50 entries at once. Declared entry length is checked before any bytes are read, which also prevents a maliciously-crafted entry from being decompressed just to find out it's oversized.

Full end-to-end streaming (`IDocumentStorage` accepting a `Stream` instead of `byte[]`, threading that through `UploadAsync` and `Inbox.razor`) is a larger, separate architectural change affecting the *existing* single-file path too, with no concrete need today. It's tracked as [#187](https://github.com/flojon/koalabooks/issues/187) rather than bundled into this feature.

## UI Change (`src/KoalaBooks.Components/Pages/Inbox.razor`)

No new button or dropzone. Minimal changes to the existing picker and upload loop:

- Extend `MudFileUpload`'s accepted extensions to include `.zip`.
- Extend the client-side per-file size guard (currently a flat 10MB check before any file is read) to apply a separate 50MB cap when the selected file's extension is `.zip`; the regular 10MB cap still applies to individually-selected pdf/png/jpg files.
- In the existing per-file loop (`UploadAsync`, currently lines 187-215): branch on file extension.
  - `.zip` → read into a `byte[]`, call `DocumentService.UploadZipAsync`, then report results with **one summary Snackbar** for the zip as a whole (e.g. "12 documents imported from example.zip, 2 skipped") rather than one Snackbar per document — a 50-entry zip must not produce 50 toasts. If any entries were skipped, list each skipped filename with its reason in the same summary (e.g. an expandable/multi-line Snackbar or inline alert below the upload control).
  - anything else → today's single-file path, unchanged (one Snackbar per file, as today).
- A zip selected alongside regular files in the same multi-select action is processed independently — each item in the picker's file list is handled by whichever branch matches its extension.

## Testing

Extend `tests/KoalaBooks.Tests/DocumentServiceTests.cs` (same conventions: real Postgres test-container DB via `TestFixture.MakeDocumentService()`), building test zips in-memory via `System.IO.Compression.ZipArchive`:

- `UploadZipAsync_ImportsAllValidEntries`
- `UploadZipAsync_SkipsInvalidEntriesAndReportsReasons` (wrong type, oversized entry) while still importing the valid ones
- `UploadZipAsync_RejectsOversizedZipContainer`
- `UploadZipAsync_RejectsZipWithTooManyEntries`
- `UploadZipAsync_FlattensNestedFolderPaths`
- `UploadZipAsync_SkipsDirectoryEntries`

## What Is Not Changing

- `DocumentService.UploadAsync` — unchanged, reused as-is per zip entry.
- `IDocumentStorage` / `DbDocumentStorage` — unchanged.
- Single-file upload UX — unchanged.
- Extraction (`CompositeExtractor`) — unchanged, runs per-entry exactly as it does for a single upload today.
