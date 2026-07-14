# Document Upload Still Buffers Full File in Memory (#236)

**Date:** 2026-07-14
**Status:** Design
**Issue:** [#236](https://github.com/flojon/koalabooks/issues/236)

## Overview

`DocumentService.UploadAsync` still reads the entire incoming file into a `byte[]` (`ReadBoundedAsync`) before calling `IDocumentStorage.SaveAsync`, even though both the browser stream ([#187](./2026-07-09-stream-uploads-design.md)) and the Postgres write path ([#235](./2026-07-13-large-object-document-storage-design.md)) are now genuinely stream-capable end to end. This removes that last buffer: `SaveAsync` moves from accepting an already-open `Stream` to a `Func<Stream> openData` factory, so the file streams straight from the browser into a Postgres Large Object with no full-file copy anywhere in the request.

## Background

#187 streamed the browser → `DocumentService` leg but stopped at `DbDocumentStorage`, which still buffered into a `byte[]` bytea column (no streaming write API existed for `bytea`). #235 closed that gap by migrating storage to Large Objects with real chunked I/O. That left one buffer standing: `DocumentService.UploadAsync` itself still calls `ReadBoundedAsync` to materialize the whole file before ever calling `storage.SaveAsync`, originally because `DbDocumentStorage.SaveAsync` needed a rewindable stream to survive a transient-failure retry (`data.CanSeek` guard — see #235's design). #208 (background extraction) separately confirmed extraction no longer runs inline in `UploadAsync`, removing the one caller that genuinely needed the full bytes synchronously mid-request.

The blocking question this design resolves: how to keep retry-safety without a rewindable in-memory copy. Answer — pass a *factory* that can re-open the source from scratch, rather than an already-open stream. A retry re-invokes the factory instead of rewinding a buffer. This works because `IBrowserFile.OpenReadStream()` is verified safe to call multiple times per file (each call re-reads the same persistent browser `Blob`, confirmed against the ASP.NET Core `BrowserFileStream`/`InputFile.ts` source), and the zip-import and Large-Object read-back paths already have their own independently re-openable sources.

## Goals / Non-Goals

- **Goal:** eliminate `DocumentService.UploadAsync`'s `ReadBoundedAsync` full-file buffer for the single-file upload path (all four call sites: `Inbox.razor`, `CustomerInvoices.razor`, `SupplierInvoices.razor`, `Journal.razor`).
- **Goal:** preserve `DbDocumentStorage.SaveAsync`'s transient-failure retry (`CreateExecutionStrategy`) without needing a seekable/rewindable stream.
- **Goal:** preserve the exact existing "Filen är för stor (max 10 MB)." UX for oversized files, now detected mid-stream instead of upfront.
- **Non-goal:** `UploadZipAsync`'s internal zip-entry handling. `ZipArchiveEntry` streams aren't re-openable from scratch the way a factory needs, and zip entries already have their own size cap (`ZipMaxBytes`, `ZipMaxEntries`) applied before per-entry extraction. It keeps materializing each entry to `byte[]` via `ReadBoundedAsync` exactly as today; only its call into `UploadAsync` adapts to the new factory-based signature by wrapping the already-buffered `byte[]` in a trivial `() => new MemoryStream(data)`.
- **Non-goal:** `IDocumentStorage.LoadAsync`/`DeleteAsync` — unchanged.

## `IDocumentStorage`

```csharp
public interface IDocumentStorage
{
    Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
```

`FileSize` moves into the return value (same pattern the existing `StorageKey` placeholder already uses) because it's only known once the bytes have actually been streamed through — there's no upfront `byte[].Length` to read anymore.

## `DbDocumentStorage.SaveAsync`

Inside the existing `CreateExecutionStrategy().ExecuteAsync` retry delegate, `openData()` is called once per attempt instead of reusing a single passed-in stream. This lets the current `CanSeek`/`Position = 0`/non-seekable-throw guard block be **deleted outright** — there's nothing to rewind; a retry just re-opens the source from scratch via the factory. `DetachTrackedDocumentData` and the transactional Large-Object write loop are otherwise unchanged; the write loop's existing per-chunk `read` count is summed into `FileSize`:

```csharp
public async Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData)
{
    var strategy = db.Database.CreateExecutionStrategy();
    return await strategy.ExecuteAsync(async () =>
    {
        DetachTrackedDocumentData(documentId);

        await using var data = openData();

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();

            var existing = await db.DocumentData.FindAsync(documentId);
            if (existing is not null)
                await ExecuteScalarAsync<int>(conn, "SELECT lo_unlink(@oid)", ("oid", NpgsqlDbType.Oid, existing.Oid));

            var oid = await ExecuteScalarAsync<uint>(conn, "SELECT lo_create(0)");
            var fd = await ExecuteScalarAsync<int>(conn, "SELECT lo_open(@oid, @mode)",
                ("oid", NpgsqlDbType.Oid, oid), ("mode", NpgsqlDbType.Integer, InvWrite));

            var buffer = new byte[ChunkSize];
            long fileSize = 0;
            int read;
            while ((read = await data.ReadAsync(buffer)) > 0)
            {
                fileSize += read;
                var chunk = buffer[..read];
                await ExecuteScalarAsync<int>(conn, "SELECT lowrite(@fd, @chunk)",
                    ("fd", NpgsqlDbType.Integer, fd), ("chunk", NpgsqlDbType.Bytea, chunk));
            }
            await ExecuteScalarAsync<int>(conn, "SELECT lo_close(@fd)", ("fd", NpgsqlDbType.Integer, fd));

            if (existing is not null) existing.Oid = oid;
            else db.DocumentData.Add(new DocumentData { DocumentId = documentId, Oid = oid });

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return (documentId.ToString(), fileSize);
        }
        catch
        {
            DetachTrackedDocumentData(documentId);
            throw;
        }
    });
}
```

Stream lifetime ownership moves here: the `await using var data = openData();` inside the retry delegate is now the sole owner of opening *and disposing* each attempt's stream. Callers (`DocumentService`, and ultimately the Razor pages) no longer open or dispose a stream themselves — they only hand over a factory.

## `DocumentService`

**New internal `MaxBytesEnforcingStream`** (small read-only decorator, lives alongside `DocumentService`) replaces `ReadBoundedAsync` for the single-file path — it enforces the cap *while* streaming instead of buffering first:

```csharp
private sealed class MaxBytesEnforcingStream(Stream inner, long maxBytes) : Stream
{
    private long _totalRead;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        _totalRead += read;
        if (_totalRead > maxBytes) throw new DocumentTooLargeException();
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
}

private sealed class DocumentTooLargeException : Exception;
```

`DocumentTooLargeException` is a plain custom exception, not an Npgsql/transient type, so `CreateExecutionStrategy`'s retry never catches it — an oversized file fails on the first attempt, not after 3 retries.

**`UploadAsync`** signature and body:

```csharp
public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Func<Stream> openData)
{
    if (currentUser.OrganisationId is null)
        return (null, "Ingen aktiv organisation.");
    if (!AllowedContentTypes.Contains(contentType))
        return (null, "Otillåten filtyp. Tillåtna typer: PDF, PNG, JPEG.");

    var doc = new Document
    {
        OrganisationId = currentUser.OrganisationId.Value,
        FileName = fileName,
        ContentType = contentType,
        FileSize = 0,
        UploadedAt = DateTime.UtcNow,
        StorageKey = ""
    };
    db.Documents.Add(doc);
    await db.SaveChangesAsync(); // gets doc.Id

    try
    {
        (doc.StorageKey, doc.FileSize) = await storage.SaveAsync(
            doc.Id, contentType, () => new MaxBytesEnforcingStream(openData(), MaxBytes));
    }
    catch (DocumentTooLargeException)
    {
        db.Documents.Remove(doc);
        await db.SaveChangesAsync();
        return (null, "Filen är för stor (max 10 MB).");
    }
    catch (Exception ex)
    {
        db.Documents.Remove(doc);
        await db.SaveChangesAsync();
        return (null, $"Lagring misslyckades: {ex.Message}");
    }

    doc.ExtractionStatus = ExtractionStatus.Pending;
    await db.SaveChangesAsync();
    extractionQueue.Enqueue(doc.Id);

    return (doc, null);
}
```

`FileSize = 0` on the initial insert is a placeholder exactly like the existing `StorageKey = ""` — both are corrected once `storage.SaveAsync` returns. No new cleanup logic is needed for a mid-stream abort (oversized or otherwise): `DbDocumentStorage.SaveAsync`'s `await using var tx` already rolls back any partial `lo_create`/`lowrite`s when an exception propagates without `CommitAsync()`, and the `Documents` row removal here is the same rollback the current code already does on storage failure.

**`UploadAndLinkAsync`** — only its parameter type changes, body is untouched:

```csharp
public async Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
    string fileName, string contentType, Func<Stream> openData, DocumentEntityType entityType, int entityId)
```

**`UploadZipAsync`** — internal zip-entry buffering is unchanged (non-goal, see above); only the call site adapts:

```csharp
var (doc, err) = await UploadAsync(entry.Name, contentType, () => new MemoryStream(data));
```

`ReadBoundedAsync` stays exactly as-is, still used for zip-entry reads — it's just no longer called from `UploadAsync` itself.

## Razor call sites

All four sites (`Inbox.razor`, `CustomerInvoices.razor`, `SupplierInvoices.razor`, `Journal.razor`) change identically — drop the `await using var stream = file.OpenReadStream(...)` local and pass a factory lambda instead, since stream lifetime is now owned inside `DbDocumentStorage`:

```csharp
// before:
await using var stream = file.OpenReadStream(maxBytes);
var (doc, err) = await DocumentService.UploadAsync(file.Name, contentType, stream);

// after:
var (doc, err) = await DocumentService.UploadAsync(file.Name, contentType, () => file.OpenReadStream(maxBytes));
```

Same shape for the three `UploadAndLinkAsync` call sites (`CustomerInvoices.razor:699-702`, `SupplierInvoices.razor:669-672`, `Journal.razor:593-596`), each replacing its `stream` local with `() => e.File.OpenReadStream(maxBytes)`.

## Error handling

- Oversized file: `DocumentTooLargeException` thrown mid-stream by `MaxBytesEnforcingStream`, caught in `UploadAsync`, same Swedish message as today, fails fast (no retry).
- Any other storage failure: existing generic `"Lagring misslyckades: ..."` path, unchanged.
- If `file.OpenReadStream(...)` itself throws when the factory is invoked (e.g. browser mis-reports file size) — propagates same as today, uncaught by the two `UploadAsync` catches (neither matches), surfaces as an unhandled error same as the current behavior for that failure mode.

## Testing

- `tests/KoalaBooks.Tests/DocumentServiceTests.cs`: `file class FailingStorage : IDocumentStorage` (line 436) updates its `SaveAsync` signature to `Func<Stream> openData` and return type to `(string, long)` — mechanical, still throws immediately to simulate a storage failure.
- New/updated `DocumentServiceTests` case for the oversized-file path: assert `UploadAsync`/`UploadAndLinkAsync` returns the "Filen är för stor" error when the source stream exceeds `MaxBytes`, and that no `Document` row is left behind.
- `DbDocumentStorage` integration tests (real Postgres container, per `TestFixture.MakeDocumentService()`): assert a factory-based `SaveAsync` round-trips correctly and that `FileSize` in the returned tuple matches the actual byte count.
- `TestFixture.MakeDocumentService(IDocumentStorage storage)` needs no change — it already accepts an `IDocumentStorage` instance; only that interface's shape changes, which callers picked up via `FailingStorage` above.
- Manual verification: drive real uploads through the running app for at least `Inbox.razor` (single file) and one `UploadAndLinkAsync` site (e.g. `SupplierInvoices.razor`), including one deliberately-oversized file, in a browser.

## Branching

Branches off `worktree-issue-208-background-extraction` (now rebased onto current `main`, so it includes #235's Large Object storage and #208's extraction-queue decoupling) rather than off `main` directly, since this design assumes both are already in place — in particular that `UploadAsync` no longer runs extraction inline. Will need re-rebasing once #208's own PR merges to `main`.

## What Is Not Changing

- `IDocumentStorage.LoadAsync`/`DeleteAsync`.
- `UploadZipAsync`'s internal zip-entry handling (still `byte[]`-based; only its `UploadAsync` call site adapts).
- `IDocumentExtractor`/`DocumentExtractionJob` — reads back via `storage.LoadAsync`, independent of `SaveAsync`'s signature change.
- Per-file/per-zip size and type limits (`MaxBytes`, `ZipMaxBytes`, `ZipMaxEntries`, `AllowedContentTypes`) — same values, same messages, just enforced mid-stream instead of upfront for the single-file path.
