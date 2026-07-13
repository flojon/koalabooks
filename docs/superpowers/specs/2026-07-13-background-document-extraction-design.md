# Run Document Text Extraction as a Background Job (#208)

**Date:** 2026-07-13
**Status:** Design

## Overview

`DocumentService.UploadAsync` currently runs `IDocumentExtractor.ExtractAsync` (PdfPig-based text extraction) synchronously inside the upload request, wrapped in a try/catch that logs and proceeds on failure. This moves extraction onto a Hangfire background job — the app already has embedded Hangfire (Postgres-backed) wired up in `Program.cs` per #209 — so a slow or pathological PDF no longer adds to the upload response's latency. Extraction stays best-effort in behavior; it just runs after the response instead of during it.

## Background

Split out of #187 (streamed uploads, already merged). #187 established that PdfPig needs a seekable, fully-materialized stream, so extraction can't run mid-stream — it runs against the bytes already read for storage. That part is unchanged here. This issue is the separate, optional step of moving that (already-materialized) extraction call off the request's hot path.

#209 (closed) investigated the mechanism and recommended Hangfire backed by the existing Postgres connection, embedded in the `Web` project so scaling web replicas scales workers safely — already merged (`e8587b1`, `8537e41`, `14bd112`). This issue implements the first real job against that infrastructure.

## New Domain Types

**`KoalaBooks.Domain.Enums.ExtractionStatus`**
```csharp
public enum ExtractionStatus { Pending, Completed, Failed }
```

**`Document.ExtractionStatus`** — new non-nullable column, enum-natural default `Pending` (value `0`). `DocumentMeta` gains the same field for the UI.

**`KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue`**
```csharp
public interface IDocumentExtractionQueue
{
    void Enqueue(int documentId);
}
```
Keeps `DocumentService` ignorant of Hangfire specifics, matching the existing `IDocumentStorage`/`IDocumentExtractor` pattern.

## Migration

Adds the `ExtractionStatus` column with default `Pending` (so any future code path that forgets to set it explicitly fails *loudly* — a document stuck showing "processing" is an obvious bug; silently marking it `Completed` would hide a forgotten enqueue). In the same migration, a one-time, explicit backfill statement sets every **pre-existing** row to `Completed` (value `1`) — these documents already went through the old synchronous extraction path in full, successfully or not:

```csharp
migrationBuilder.AddColumn<int>(
    name: "ExtractionStatus",
    table: "Documents",
    type: "integer",
    nullable: false,
    defaultValue: 0); // Pending

migrationBuilder.Sql("UPDATE \"Documents\" SET \"ExtractionStatus\" = 1"); // Completed, one-time backfill of existing rows only
```

This is deliberately not a DB-level default of `Completed` — that would apply to every future insert that omits the column too, not just historical rows.

## `DocumentService` Changes

`UploadAsync` drops the `IDocumentExtractor` constructor dependency (extraction no longer happens here) and gains `IDocumentExtractionQueue`. After `storage.SaveAsync` succeeds:

```csharp
doc.ExtractionStatus = ExtractionStatus.Pending;
await db.SaveChangesAsync();
extractionQueue.Enqueue(doc.Id);
return (doc, null);
```

The synchronous `try { extractor.ExtractAsync(...) } catch { log }` block is removed entirely from `UploadAsync`. `UploadZipAsync` needs no changes — it already calls `UploadAsync` per entry, so each extracted document gets queued independently.

## `DocumentExtractionJob`

New `KoalaBooks.Application.Jobs.DocumentExtractionJob`, constructor-injected `AppDbContext`, `IDocumentStorage`, `IDocumentExtractor`, `ILogger<DocumentExtractionJob>`. Hangfire resolves it per-invocation via its ASP.NET Core service-scope integration (`Hangfire.AspNetCore`, already referenced by `Web`).

```csharp
[AutomaticRetry(Attempts = 3)]
public async Task RunAsync(int documentId)
{
    // IgnoreQueryFilters: this job has no HttpContext, so ICurrentUser.OrganisationId
    // is always null and the tenant query filter would hide every document. Safe here
    // because the job only ever acts on a documentId handed to it by trusted code that
    // just created that exact row — not arbitrary tenant-crossing input.
    var doc = await db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == documentId);
    if (doc is null) return;

    var data = await storage.LoadAsync(doc.StorageKey); // storage/DB failures bubble → Hangfire retries (Attempts = 3)

    try
    {
        var result = await extractor.ExtractAsync(doc.FileName, doc.ContentType, data);
        doc.SuggestedType = result.SuggestedType;
        doc.ExtractedDataJson = result.SuggestedType is not null ? JsonSerializer.Serialize(result) : null;
        doc.DocumentDate = result.InvoiceDate;
        doc.ExtractionStatus = ExtractionStatus.Completed;
    }
    catch (Exception ex)
    {
        // Content-level failure (e.g. malformed PDF) — retrying won't help, same file fails the same way.
        logger.LogWarning(ex, "Extraction failed for {FileName} — proceeds without suggestion", doc.FileName);
        doc.ExtractionStatus = ExtractionStatus.Failed;
    }

    await db.SaveChangesAsync();
}
```

**Failure-handling split:**
- `extractor.ExtractAsync` throwing → caught immediately, `ExtractionStatus = Failed`, no retry (content-specific; a bad PDF fails the same way every attempt).
- `storage.LoadAsync` throwing → left to bubble, so `[AutomaticRetry(Attempts = 3)]` covers transient storage/DB hiccups reading back a file just written moments earlier.
- **Scope cut:** if all 3 storage-retry attempts are exhausted (rare — infra outage territory), the document is left `Pending` and shows up as a failed job in the `/hangfire` dashboard rather than syncing that terminal state back into `Document.ExtractionStatus`. Closing that gap would need a Hangfire state-change filter/hook; not justified for best-effort metadata on an edge case this narrow.

## Queue Registration (`Program.cs`)

Alongside the existing `if (!builder.Environment.IsEnvironment("Testing"))` Hangfire block:

```csharp
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfire(...);
    builder.Services.AddHangfireServer();
    builder.Services.AddScoped<IDocumentExtractionQueue, HangfireDocumentExtractionQueue>();
}
else
{
    builder.Services.AddScoped<IDocumentExtractionQueue, NoOpDocumentExtractionQueue>();
}
```

`HangfireDocumentExtractionQueue` (`KoalaBooks.Infrastructure.Services`) wraps `IBackgroundJobClient.Enqueue<DocumentExtractionJob>(j => j.RunAsync(documentId))`. `NoOpDocumentExtractionQueue` is a trivial do-nothing implementation, needed because `DocumentService` is resolved via DI even under the `Testing` environment (e.g. the `GET /documents/{id}` minimal API endpoint) where Hangfire itself is disabled.

## Packages

Neither `KoalaBooks.Application` nor `KoalaBooks.Infrastructure` currently reference any Hangfire package. Both need `Hangfire.Core` added directly:
- `Application` — for `[AutomaticRetry]` on `DocumentExtractionJob`.
- `Infrastructure` — for `IBackgroundJobClient` in `HangfireDocumentExtractionQueue`.

`Hangfire.Core` is the lightweight package (attributes, `IBackgroundJobClient`, job storage abstractions) with no server/storage dependencies — it doesn't pull in Postgres or the dashboard.

## `Inbox.razor`

`DocumentMeta.ExtractionStatus` renders as a small "Bearbetar…" badge in the type column while `Pending` (alongside the existing `ClassifiedType` badge, which is orthogonal — a doc can be extraction-`Pending` and un-classified at the same time).

A polling timer re-runs `LoadPageAsync` every ~5s while any document on the current page is `Pending`, and stops once none are — avoids indefinite polling once a page settles. This is a deliberate choice over real-time push (SignalR): the app embeds Hangfire per web instance with a multi-replica deployment as the near-term goal, so a job completing on one pod needs to reach a browser circuit connected to a different pod — genuine cross-instance push needs a backplane (Redis), which is new infrastructure #209 specifically avoided when it picked Postgres-backed Hangfire over a broker-dependent alternative. Polling re-reads the same shared Postgres state instead, so it's correct regardless of which pod ran the job. Filed as a deliberate future improvement: **#237** (add a Redis SignalR backplane and replace this polling with push).

`ClassifyDocumentDialog`/`PreviewDocumentDialog` need no changes — they already handle `Doc.SuggestedType`/`Doc.ExtractedDataJson` being `null` gracefully (that's the existing "no suggestion found" path, which now also covers "still pending").

## Test Changes

Existing `DocumentServiceTests` cases that stub `IDocumentExtractor` and assert `doc.SuggestedType`/`doc.DocumentDate` immediately after `UploadAsync` (`UploadAsync_SetsSuggestedTypeFromFilename_ClassifiedTypeRemainsNull`, `UploadAsync_PopulatesDocumentDateFromExtractor`) no longer apply — extraction isn't synchronous. Those move to a new `DocumentExtractionJobTests` that constructs the job directly and calls `RunAsync`. `DocumentServiceTests` instead:
- Asserts `doc.ExtractionStatus == ExtractionStatus.Pending` right after `UploadAsync`.
- Asserts the injected `IDocumentExtractionQueue` (fake/spy) was called with the new document's id.

`TestFixture.MakeDocumentService(...)` overloads updated to supply a fake `IDocumentExtractionQueue` (a simple recording stub, not Hangfire) alongside existing `IDocumentStorage`/`IDocumentExtractor` overloads.

## Non-Goals (unchanged from the issue)

- Not changing how `IDocumentStorage`/`DocumentService.UploadAsync` handle the byte stream itself (#187's territory).
- Not implementing real-time push for the pending→done transition (#237).
- Not syncing Hangfire's terminal-failure state back into `Document.ExtractionStatus` after retries are exhausted (see Scope cut above).
