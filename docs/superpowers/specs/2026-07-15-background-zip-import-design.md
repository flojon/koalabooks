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
1. Copies `openZipData()` into a local temp file (never fully buffered in memory), enforcing `ZipMaxBytes = 500MB` as a running byte count during the copy — aborts and deletes the temp file immediately if exceeded, before touching Postgres at all.
2. Opens `ZipArchive` on that local temp `FileStream` just to read `Entries.Count`, enforcing `ZipMaxEntries = 500` — aborts and deletes the temp file if exceeded. This is the one piece of validation still synchronous in the request — both limits fail fast with an error before any Postgres LO, batch, or job is created, matching the issue's requirement that the whole zip is rejected upfront on either cap.
3. Copies the local temp file into a new Postgres LO via the shared copy-stream-into-new-LO helper (§4) — this LO is the durable staging record from here on. Deletes the local temp file (its only purpose was validation + serving as the LO write's source).
4. Creates a `ZipImportBatch` row (§5) with `Done = false`, `Acknowledged = false`, `StagingOid` from step 3, `TotalEntries` from step 2, and other counts at zero.
5. Enqueues `ZipImportJob(batchId)` via `IZipImportQueue` (Hangfire-backed / no-op for tests, mirroring `IDocumentExtractionQueue`/`NoOpDocumentExtractionQueue`).
6. Returns immediately. `Inbox.razor` shows a "Zip accepted — processing in background" Snackbar.

The per-file 10MB cap is enforced automatically by `UploadAsync`'s `MaxBytesEnforcingStream` (from #236) when each entry is later uploaded inside `ZipImportJob` — `UploadZipAsync` does not need its own copy of that check.

### 2. `ZipImportJob` (Hangfire job)

```csharp
public class ZipImportJob(AppDbContext db, DocumentService documentService)
{
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int batchId) { ... }
}
```

- Loads the `ZipImportBatch` (`IgnoreQueryFilters()`, no `ICurrentUser` in job context — same reasoning as `DocumentExtractionJob`).
- Copies the batch's staged LO into a fresh local temp file via the shared copy-LO-into-stream helper (§4) — a single bounded, sequential copy, not a transaction held open for the rest of the method. Opens `ZipArchive` on that local `FileStream` (ordinary `System.IO.FileStream`, fully `Seek`-capable — no Postgres-backed stream needed for the rest of processing). `TotalEntries` was already set at upload time (§1 step 4); the job doesn't re-derive it.
- **Resumes from `ProcessedEntries`**: iterates `archive.Entries.Skip(batch.ProcessedEntries)` rather than from the start, so a Hangfire retry after a mid-batch failure picks up where the last attempt left off instead of re-importing already-processed entries as duplicates.
- For each remaining entry:
  - Unsupported type / directory entry / over the 10MB-per-file cap (surfaced as a thrown exception from `UploadAsync`'s `MaxBytesEnforcingStream`) → append `{FileName, Reason}` to `SkippedReasons`, increment `SkippedCount`.
  - Otherwise: `documentService.UploadAsync(entry.Name, contentType, () => archive.GetEntry(entry.FullName)!.Open())`. No per-entry `MemoryStream` buffering — `UploadAsync`'s retry story (from #236) already handles re-invoking the factory to reopen the entry if a save attempt fails transiently. On success, increment `ImportedCount`.
  - Increment `ProcessedEntries` and save the batch row after every entry, so progress advances incrementally rather than only at the end, and so a retry resumes at the right offset.
- In a `finally`, deletes the local temp file regardless of outcome.
- On completion (or after exhausting retries — see Error Handling): set `Done = true`, delete the staging LO (the durable Postgres copy is only removed once the batch is fully resolved, successful or not — never on a mid-batch attempt that might still retry).

### 3. UI polling (`Inbox.razor`)

Extend the existing #208 poll-timer to also query `ZipImportBatch` rows for the current organisation where `Acknowledged = false` (covers both still-running batches and finished-but-not-yet-shown-to-the-user ones). No change to how individual documents are displayed — they already appear via the existing `Pending → Completed` flow as soon as `UploadAsync` creates each row, so entries reveal one by one as the job works through the archive. When a polled batch has `Done = true`, show one summary Snackbar:

> "Import finished: 47 imported, 3 skipped: invoice-x.exe (unsupported type), ..."

then call a new `AcknowledgeZipBatchAsync(batchId)` (sets `Acknowledged = true`) so it drops out of the next poll and the summary fires exactly once. The poll-timer's keep-alive condition extends to: any visible document is `Pending`, **or** any unacknowledged batch exists (running or freshly done).

### 4. Shared Postgres LO helpers

Two of `DbDocumentStorage`'s existing raw-SQL chunk loops (`lo_create`/`lo_open`/`lowrite`/`loread`/`lo_close` — plain PostgreSQL functions called via SQL, *not* Npgsql's `NpgsqlLargeObjectManager`/`NpgsqlLargeObjectStream`, which are `[Obsolete]` as of Npgsql 8.0 specifically in favor of calling these functions directly) are extracted into two shared, sequential-only helpers:

- `CopyStreamIntoNewLargeObjectAsync(NpgsqlConnection conn, Stream source) -> uint oid` — the write direction (`DbDocumentStorage.SaveAsync`'s existing loop, generalized off `documentId`/`DocumentData` to just return the new oid).
- `CopyLargeObjectIntoStreamAsync(NpgsqlConnection conn, uint oid, Stream destination)` — the read direction (`DbDocumentStorage.LoadAsync`'s existing loop, generalized to write into an arbitrary destination `Stream` — e.g. a local `FileStream` — instead of only ever assembling a `byte[]`).

Neither helper needs `Seek`: both are single forward passes. `ZipArchive`'s `Seek` requirement (to read the central directory and jump into entries) is satisfied entirely by the ordinary, fully-`Seek`-capable local `FileStream` that `ZipImportJob` copies the LO into (§2) — no Postgres-backed stream is ever handed to `ZipArchive`, and no long-lived transaction is held open across batch processing.

- `DbDocumentStorage` is refactored to call these two helpers internally instead of its own inline loops; its external interface and behavior (`SaveAsync`/`LoadAsync`/`DeleteAsync`) are unchanged.
- The zip staging write path (§1 step 3) and read path (`ZipImportJob`, §2) both call these helpers directly, keyed by `ZipImportBatch.StagingOid` (a raw LO oid, no separate staging table/interface) — this avoids a parallel `IZipStagingStorage` abstraction that would otherwise duplicate `DbDocumentStorage`'s chunk-loop logic a second time.

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
| `Acknowledged` | bool | set once `Inbox.razor` has shown the summary Snackbar for a `Done` batch; drives it out of future polls |
| `CreatedAt` | datetime | |

### 6. Error handling

- **Corrupt zip container** (fails to open as a `ZipArchive`): batch is marked `Done = true` immediately, with a single `SkippedReasons` entry describing the failure, `TotalEntries = 0`. No entries are processed.
- **Corrupt/mid-read entry**: caught and skipped, same as today's per-entry behavior — batch continues to the next entry.
- **Mid-batch job failure + Hangfire retry**: the retried attempt resumes from `ProcessedEntries` (§2) rather than reprocessing already-imported entries, so retries can't create duplicate `Document` rows.
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
- Retry resumption: simulate a mid-batch failure after N entries, rerun `RunAsync`, assert entries `0..N-1` aren't re-imported (no duplicate `Document` rows) and processing continues from entry `N`.
- New limits: reject at 501 entries / just over 500MB during upload, accept at the boundary.
- The two shared Postgres LO helpers (§4): round-trip a stream through `CopyStreamIntoNewLargeObjectAsync` then `CopyLargeObjectIntoStreamAsync` and assert byte-for-byte equality.

## Non-goals

- No change to the 10MB per-file/per-entry size limit.
- No batch-level progress bar / percentage UI — documents revealing one by one via the existing per-document status display is the only in-progress feedback, per the "batch summary is for the final toast only" decision.
- No parallel/fan-out processing of zip entries (one Hangfire job per entry) — entries are processed sequentially within a single `ZipImportJob`. Revisit only if real-world timings at 500 entries show this is a bottleneck.
