# Design: Background-process zip inbox imports (#207)

## Summary

`DocumentService.UploadZipAsync` currently processes a zip inbox upload synchronously, inside one blocking request: the whole container is buffered into memory, opened as a `ZipArchive`, and every entry is read, validated, and stored one at a time before the call returns. This caps practical batch size at `ZipMaxEntries = 50` / `ZipMaxBytes = 50MB` — not because of the per-file size limit (10MB, unchanged), but because all the work happens inside one request and the whole container must fit in memory at once.

This design moves zip processing onto a Hangfire background job, streams the container instead of buffering it, and raises the entries-per-zip target to 500 documents / 500MB.

## Dependency

**Blocked on #236** ("stream uploads through to storage") merging to `main` first. #236 changes `DocumentService.UploadAsync` and `IDocumentStorage.SaveAsync` from `Stream data` to `Func<Stream> openData`, and this design's `ZipImportJob` targets that post-#236 signature directly — it is not written against the current `main` signature. Implementation should not start until #236 has merged.

## Current state (as of #193/#208/#236-in-flight)

- `DocumentService.UploadZipAsync(byte[] zipData)` (`src/KoalaBooks.Application/Services/DocumentService.cs`) reads the whole zip into memory, opens `ZipArchive` on a `MemoryStream`, and loops entries sequentially — each entry is itself read into a `byte[]` before being passed to `UploadAsync`.
- `#208` already built the pattern this design reuses: `Document.ExtractionStatus` (`Pending`/`Completed`/`Failed`), `IDocumentExtractionQueue` → `HangfireDocumentExtractionQueue`/`NoOpDocumentExtractionQueue` → `DocumentExtractionJob` (`[AutomaticRetry(Attempts=3)]`, `IgnoreQueryFilters()` since jobs have no `ICurrentUser`), and a 5-second poll-timer in `Inbox.razor` that refreshes while any visible document is `Pending`.
- `#236` (in flight, unmerged) changes `UploadAsync`/`SaveAsync` to accept `Func<Stream> openData` instead of `Stream data`, so a failed/retried save can re-invoke the factory for a fresh stream rather than requiring `Seek`. It also introduces a `MaxBytesEnforcingStream` wrapper inside `UploadAsync` that enforces the 10MB per-file cap during the streamed read, replacing the old buffer-then-check approach. It does not touch `UploadZipAsync`'s container-level or entry-level buffering.
- `DbDocumentStorage` stores document bytes as Postgres Large Objects, using raw `lo_create`/`lo_open`/`lowrite`/`loread`/`lo_close`/`lo_unlink` SQL calls wrapped in an EF Core execution-strategy retry loop. `LoadAsync` reads a LO in chunks but assembles the result into one `byte[]` before returning — there is no existing `Read`+`Seek`-capable `Stream` over an LO anywhere in the codebase.
- No existing table or interface represents a transient "batch upload in progress"; `ZipImportBatch` (below) is new.

## Architecture

### 1. Upload path (`Inbox.razor` → `DocumentService`)

`Inbox.razor` passes the picked zip's `IBrowserFile.OpenReadStream()` (wrapped in a factory, to match the reopen-on-retry convention from #236) to:

```csharp
Task<(int? BatchId, string? Error)> UploadZipAsync(Func<Stream> openZipData, ...)
```

`UploadZipAsync`:
1. Streams `openZipData()` directly into a Postgres LO via the shared `LargeObjectStream` write path (§4) — the container is never fully buffered in memory. Enforces `ZipMaxBytes = 500MB` as a running byte count during this copy; aborts and deletes the LO immediately if exceeded.
2. Reopens the staged LO via a `LargeObjectStream` in read mode, wraps it in `ZipArchive.Open(stream, ZipArchiveMode.Read)` just to read `Entries.Count`, then closes it (no entry processing here). Enforces `ZipMaxEntries = 500`; aborts and deletes the LO if exceeded. This is the one piece of validation still synchronous in the request — both limits fail fast with an error before any batch/job is created, matching the issue's requirement that the whole zip is rejected upfront on either cap.
3. Creates a `ZipImportBatch` row (§5) with `Done = false`, `TotalEntries` set from the count just read in step 2, and other counts at zero.
4. Enqueues `ZipImportJob(batchId)` via `IZipImportQueue` (Hangfire-backed / no-op for tests, mirroring `IDocumentExtractionQueue`/`NoOpDocumentExtractionQueue`).
5. Returns immediately. `Inbox.razor` shows a "Zip accepted — processing in background" Snackbar.

The per-file 10MB cap is enforced automatically by `UploadAsync`'s `MaxBytesEnforcingStream` (from #236) when each entry is later uploaded inside `ZipImportJob` — `UploadZipAsync` does not need its own copy of that check.

### 2. `ZipImportJob` (Hangfire job)

```csharp
public class ZipImportJob(AppDbContext db, IDocumentService documentService)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int batchId) { ... }
}
```

- Loads the `ZipImportBatch` (`IgnoreQueryFilters()`, no `ICurrentUser` in job context — same reasoning as `DocumentExtractionJob`).
- Opens the batch's staged LO via a `LargeObjectStream` in read mode (§4) and passes it to `ZipArchive.Open(stream, ZipArchiveMode.Read)`. This requires `Seek` — reading the central directory and each entry's local header depends on it — which is exactly what `LargeObjectStream` provides and `DbDocumentStorage.LoadAsync`'s byte[]-draining approach does not.
- `TotalEntries` was already set at upload time (§1 step 3); the job doesn't re-derive it.
- Loops entries sequentially. For each entry:
  - Unsupported type / directory entry / over the 10MB-per-file cap (surfaced as a thrown exception from `UploadAsync`'s `MaxBytesEnforcingStream`) → append `{FileName, Reason}` to `SkippedReasons`, increment `SkippedCount`.
  - Otherwise: `documentService.UploadAsync(entry.Name, contentType, () => archive.GetEntry(entry.FullName)!.Open())`. No per-entry `MemoryStream` buffering — `UploadAsync`'s retry story (from #236) already handles re-invoking the factory to reopen the entry if a save attempt fails transiently. On success, increment `ImportedCount`.
  - Increment `ProcessedEntries` and save the batch row after every entry, so progress advances incrementally rather than only at the end.
- On completion (or after exhausting retries — see Error Handling): set `Done = true`, delete the staging LO.

### 3. UI polling (`Inbox.razor`)

Extend the existing #208 poll-timer to also query `ZipImportBatch` rows for the current organisation where `Done = false`. No change to how individual documents are displayed — they already appear via the existing `Pending → Completed` flow as soon as `UploadAsync` creates each row, so entries reveal one by one as the job works through the archive. When a batch flips to `Done = true`, show one summary Snackbar:

> "Import finished: 47 imported, 3 skipped: invoice-x.exe (unsupported type), ..."

and stop polling that batch (e.g. track seen/acknowledged batch IDs client-side so the summary fires once).

### 4. Shared Postgres LO primitive

Extract a `LargeObjectStream : Stream` from `DbDocumentStorage`'s existing raw SQL calls — a genuine `Read`+`Seek`-capable stream over an open Large Object, implementing `Seek` via `lo_lseek`, `Read` via `loread`, `Write` via `lowrite`, holding its connection/transaction open for the stream's lifetime (disposed via `DisposeAsync`).

- `DbDocumentStorage` is refactored to compose this internally; its external interface and behavior (`SaveAsync`/`LoadAsync`/`DeleteAsync`) are unchanged.
- The zip staging write path (upload) and read path (`ZipImportJob` opening the container) both use `LargeObjectStream` directly, keyed by `ZipImportBatch.StagingOid` (a raw LO oid, no separate staging table). This avoids a parallel `IZipStagingStorage` interface/implementation that would otherwise duplicate `DbDocumentStorage`'s chunked LO read/write logic.
- This is new capability, not a refactor-only change: nothing in the codebase today needs random-access reads into an LO, since `DbDocumentStorage.LoadAsync` always drains sequentially into a `byte[]` (fine for single documents, capped at 10MB — not fine for a 500MB zip container).

### 5. Data model additions

New `ZipImportBatch` entity + migration:

| Column | Type | Notes |
|---|---|---|
| `Id` | int (PK) | |
| `OrganisationId` | int | for tenant-scoped polling |
| `StagingOid` | uint | raw Postgres LO oid holding the zip container; null/cleared once deleted |
| `TotalEntries` | int | set once the archive is opened |
| `ProcessedEntries` | int | incremented per entry |
| `ImportedCount` | int | |
| `SkippedCount` | int | |
| `SkippedReasons` | jsonb | `{FileName, Reason}[]` |
| `Done` | bool | terminal state, including failure (see below) |
| `CreatedAt` | datetime | |

### 6. Error handling

- **Corrupt zip container** (fails to open as a `ZipArchive`): batch is marked `Done = true` immediately, with a single `SkippedReasons` entry describing the failure, `TotalEntries = 0`. No entries are processed.
- **Corrupt/mid-read entry**: caught and skipped, same as today's per-entry behavior — batch continues to the next entry.
- **Job-level exception exhausting Hangfire's 3 retries**: the batch must not poll forever. `ZipImportJob` needs a way to reach `Done = true` (marked failed) even when `RunAsync` itself throws after retries are exhausted — via a Hangfire `IElmahFilter`/failure filter attached to the job, or a try/catch around the job body that marks the batch failed before rethrowing (so Hangfire's dashboard still records the failure) — exact mechanism to be settled in the implementation plan.

### 7. New limits

`ZipMaxEntries = 500`, `ZipMaxBytes = 500 * 1024 * 1024` (500MB). Per-file 10MB cap unchanged (non-goal, per the issue).

## Testing

Following `DocumentExtractionJobTests.cs`'s pattern — construct `ZipImportJob` directly against a Testcontainer Postgres + stub `IDocumentService`/extractor, call `RunAsync`, assert `ZipImportBatch` and `Document` rows afterward:

- Happy path: multiple valid entries all imported, counts correct.
- Skip accumulation: mixed valid/invalid/oversized entries, correct `SkippedReasons`.
- Corrupt zip container: immediate `Done = true`, no entries processed.
- Corrupt entry mid-batch: skipped, batch continues, later entries still processed.
- Retry/failure terminal state: job throws past retry limit → batch still reaches `Done = true` (failed), not stuck.
- New limits: reject at 501 entries / just over 500MB during upload, accept at the boundary.
- `LargeObjectStream` itself: `Read`/`Seek`/`Write` round-trip correctness, including seeking backward and forward within a single LO (needed for `ZipArchive` to read the central directory and then jump into entries).

## Non-goals

- No change to the 10MB per-file/per-entry size limit.
- No batch-level progress bar / percentage UI — documents revealing one by one via the existing per-document status display is the only in-progress feedback, per the "batch summary is for the final toast only" decision.
- No parallel/fan-out processing of zip entries (one Hangfire job per entry) — entries are processed sequentially within a single `ZipImportJob`. Revisit only if real-world timings at 500 entries show this is a bottleneck.
