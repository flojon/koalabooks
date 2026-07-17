# Design: Shared background-job infrastructure

## Summary

Four open issues (#279 SIE import, #280 BAS import, #281 year-end close, #282 archive export) each independently say "follow the pattern established by document extraction and zip import" — but that pattern has never been factored out. Today it exists twice, bespoke both times: `Document.ExtractionStatus` + `IDocumentExtractionQueue`/`HangfireDocumentExtractionQueue`/`DocumentExtractionJob` (#208, merged), and `ZipImportBatch` + `IZipImportQueue`/`HangfireZipImportQueue`/`ZipImportJob` (#207, PR #251 open/unmerged) — each with its own status entity, its own tenant-bootstrap boilerplate in the job class, and its own hand-rolled poll-timer/staleness-cutoff/acknowledge block in `Inbox.razor`.

This design extracts the reusable parts before #279–282 add four more copies of the same duplication: a generic `BackgroundJobRun` status table, a shared job-side base class for the tenant-bootstrap dance, and a shared Blazor component for the poll/staleness/acknowledge dance. `ZipImportJob`/`ZipImportBatch` (PR #251, still unmerged) is retrofitted onto the new pattern before that PR merges, rather than merging first and migrating later. `DocumentExtractionJob` keeps `Document.ExtractionStatus` as a per-row field rather than moving to `BackgroundJobRun` — it isn't a batch/job record, it's a status on the entity the job acts on — and keeps its current DI-injected `AppDbContext` unchanged, since it has no tenant-scoping need to extract (see #2).

## Current state

- `IDocumentExtractionQueue` → `HangfireDocumentExtractionQueue`/`NoOpDocumentExtractionQueue` → `DocumentExtractionJob` (`[AutomaticRetry(Attempts=3)]`, `IgnoreQueryFilters()`, `SaveChangesResolvingConcurrencyAsync` for the `DocumentDate` race) sets `Document.ExtractionStatus` (`Pending`/`Completed`/`Failed`) directly on the row it processes. `Inbox.razor` polls via a `System.Threading.Timer` (5s interval) that keeps running while any visible document is `Pending` and younger than `PendingStaleAfter` (10 min).
- `IZipImportQueue` → `HangfireZipImportQueue`/`NoOpZipImportQueue` → `ZipImportJob` (PR #251, not yet merged) builds its own `LocalCurrentUser`-scoped `AppDbContext` (jobs have no `HttpContext`, so a DI-resolved `ICurrentUser` is always null — see the Hangfire/`ICurrentUser` note from #207), processes a staged zip against a `ZipImportBatch` row (`TotalEntries`/`ProcessedEntries`/`ImportedCount`/`SkippedCount`/`SkippedReasonsJson`/`Done`/`Acknowledged`), and wraps LO copy work in `CreateExecutionStrategy`. `Inbox.razor` extends the same poll-timer to also watch unacknowledged batches (`ZipBatchStaleAfter`), and shows a one-shot summary Snackbar on completion via `AcknowledgeZipBatchAsync`.
- #279–282 each propose adding a `<Feature>Job` + `Hangfire<Feature>Queue` pair "mirroring" the above, with status reporting left vague ("via the Hangfire dashboard or a lightweight status check", "surface completion"). None of them specify a shared shape, so each would likely re-derive the tenant-bootstrap and poll-timer logic independently.
- Existing pages that will host status reporting for the four new jobs already exist: `SieImport.razor`, `SieExport.razor`, `Accounts.razor` (BAS import), `FiscalYears.razor` (year-end close). No new "background jobs" overview page is needed.

## Architecture

### 1. `BackgroundJobRun` (generic status entity)

```csharp
public enum BackgroundJobType { ZipImport, SieImport, BasImport, YearEndClose, SieExport }
public enum BackgroundJobStatus { Pending, Running, Completed, Failed }

public class BackgroundJobRun
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public BackgroundJobType JobType { get; set; }
    public BackgroundJobStatus Status { get; set; }
    public int ProcessedCount { get; set; }
    public int? TotalCount { get; set; }          // null where progress isn't meaningful (e.g. BAS import)
    public string? ResultJson { get; set; }         // job-specific payload: skip reasons, output document key, ...
    public bool Acknowledged { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

One table, one migration, replaces `ZipImportBatch` and the four status entities #279–282 would otherwise each add. `ResultJson` carries whatever shape a given job needs to report — e.g. zip/SIE import's `{ImportedCount, SkippedCount, SkippedReasons}`, archive export's `{DocumentStorageKey}` (per #282, export output is persisted "via the existing document storage" and needs to be fetchable once done) — read by the page that knows how to render it, not by shared code.

`Document.ExtractionStatus` is explicitly **not** migrated onto this table — it's a field on the entity the job produces, not a batch record, and moving it would force an awkward join for no benefit.

A composite index on `(OrganisationId, JobType, Acknowledged)` is required — it's the shape of the "open/unacknowledged runs for this org+JobType" query that `IBackgroundJobRunService.GetOpenRunsAsync` runs (filtering on `Acknowledged`, not `Status` — a completed-but-unacknowledged run must still be returned so the poller can fire `OnRunCompleted` for it), and that the poller (#5) hits on every 5s tick for every open job on every visible page.

### 2. Tenant-bootstrap helper

Only jobs that call a tenant-scoped service (`DocumentService`, and the four new jobs' equivalents) need this — `ZipImportJob` today, not `DocumentExtractionJob` (see below). Jobs have no `HttpContext`, so a DI-resolved `ICurrentUser` is always null and any tenant-scoped write would be rejected. `ZipImportJob` works around this today by hand: construct an `AppDbContext` bound to a mutable `LocalCurrentUser` with no org set yet, look up its batch row via `IgnoreQueryFilters()` (unaffected by the still-empty tenant), then set `tenant.OrganisationId` from the loaded row so every subsequent query/write on that same context is correctly scoped from that point on.

A small static factory (e.g. `JobTenantContext.CreateUnscoped(DbContextOptions<AppDbContext> options)`) extracts exactly that two-phase pattern — it hands back both the `AppDbContext` and the mutable `LocalCurrentUser` it's bound to, so the caller can do its own `IgnoreQueryFilters()` lookup first and only set `tenant.OrganisationId` once the row's org is known. `BackgroundJobRunBase` (#3) wraps this for the five `BackgroundJobRun`-based jobs.

`DocumentExtractionJob` is **not** retrofitted onto this helper. It never calls a tenant-scoped service — it only mutates the single `Document` row it already loaded via `IgnoreQueryFilters()` on its constructor-injected `AppDbContext` — so there's no tenant-scoping need to extract, and no duplication with `ZipImportJob` to remove. It keeps its current DI-injected context unchanged.

### 3. `BackgroundJobRunBase`

An abstract base in `KoalaBooks.Application.Jobs`, built on #2, for the five jobs that use `BackgroundJobRun` (zip import + the four new ones):

- `LoadRunAsync(int runId)` — `IgnoreQueryFilters()` lookup; returns null if missing or `Status` is already `Completed`/`Failed`. On a successful load, sets `Status = Running` and saves before returning, so a run picked up by Hangfire immediately reflects as in-progress to the poller rather than sitting at `Pending` for the entire job duration.
- Progress save helper — bumps `ProcessedCount` and saves, same incremental-save-per-entry approach `ZipImportJob` already uses so a Hangfire retry resumes from where it left off instead of reprocessing.
- `CompleteAsync(BackgroundJobStatus status, object? resultPayload)` — serializes `resultPayload` into `ResultJson`, sets `Status`/`Acknowledged = false`.

Concrete jobs (`SieImportJob`, `BasImportJob`, `YearEndCloseJob`, `SieExportJob`, and the refactored `ZipImportJob`) inherit this and implement only their business logic in `RunAsync`. `DocumentExtractionJob` does not inherit `BackgroundJobRunBase` and does not use the tenant-bootstrap helper (#2) — it has no `BackgroundJobRun` row to manage and no tenant-scoping need, so nothing here applies to it.

### 4. `IBackgroundJobRunService`

An Application-layer service wrapping the `BackgroundJobRun` queries every job and every page currently needs: create a run, get open/unacknowledged runs for an org+`JobType`, acknowledge a run. Both `BackgroundJobRunBase` (server-side, tenant-scoped context) and the Blazor poller (#5, request-scoped context via normal DI) go through this instead of writing ad-hoc LINQ against `AppDbContext` in each Razor page, which is how `Inbox.razor` does it today.

### 5. `BackgroundJobStatusPoller` (Blazor component)

Extracted from `Inbox.razor`'s existing `_pollTimer`/`Interlocked`/`Dispose` block, generalized to any `BackgroundJobType`:

```razor
<BackgroundJobStatusPoller JobType="BackgroundJobType.SieImport"
                            OrganisationId="@OrganisationId"
                            StaleAfter="TimeSpan.FromMinutes(10)"
                            OnRunCompleted="HandleImportCompleted" />
```

Owns the timer lifecycle (5s default interval, configurable), the staleness cutoff (stops polling once every open run is older than `StaleAfter`, matching today's `PendingStaleAfter`/`ZipBatchStaleAfter`), and calls `IBackgroundJobRunService.AcknowledgeAsync` once the host page's `OnRunCompleted` callback returns. The host page owns only what's actually feature-specific: how to render the toast (or, for archive export, a download link built from `ResultJson`'s document key) from a completed `BackgroundJobRun`.

`Inbox.razor` adopts this component for both its existing polled statuses (`Document.ExtractionStatus` stays a direct query since it isn't a `BackgroundJobRun`; the zip-batch half of its polling is replaced by `<BackgroundJobStatusPoller JobType="ZipImport" .../>`).

### 6. Queue interfaces — unchanged pattern

`ISieImportQueue`, `IBasImportQueue`, `IYearEndCloseQueue`, `ISieExportQueue` are each added the same way as `IZipImportQueue`: a narrow, single-method interface (`Enqueue(int runId)`) with a `Hangfire...Queue` implementation and a `NoOp...Queue` test double. This isn't consolidated into one generic queue interface — it continues the narrow-queue-interface precedent `IDocumentExtractionQueue` established in #208 and `IZipImportQueue` continued in #207 (each service depends only on the one queue capability it needs), and the Hangfire wrapper is 4 lines regardless.

## Data flow (SIE import, representative of the four new jobs)

1. User uploads a file on `SieImport.razor`. The upload handler stages the file (reusing #251's shared Postgres-LO copy helpers — `CopyStreamIntoNewLargeObjectAsync`/`CopyLargeObjectIntoStreamAsync`), creates a `BackgroundJobRun(JobType = SieImport, Status = Pending)`, and enqueues via `ISieImportQueue`.
2. `SieImportJob : BackgroundJobRunBase` loads the run, gets a tenant-scoped `AppDbContext` via the bootstrap helper, runs the import updating `ProcessedCount`/`TotalCount` incrementally, and calls `CompleteAsync(Completed, new { ImportedCount, SkippedCount, SkippedReasons })` (or `Failed` — see Error handling).
3. `SieImport.razor` hosts `<BackgroundJobStatusPoller JobType="SieImport" ... OnRunCompleted="ShowImportSummary" />`. When the run completes, the callback deserializes `ResultJson` and shows the page's own Snackbar text; the poller then acknowledges the run.

Archive export (#282) differs only in step 3: `OnRunCompleted` reads a document storage key out of `ResultJson` and renders a download link instead of a plain summary.

## Error handling

Today, a batch/document that exhausts Hangfire's 3 retries is left `Done = false`/`Pending` forever — nothing ever marks it `Failed`; the UI only stops showing it once it ages past the client-side staleness cutoff, so a permanently-stuck job silently disappears rather than reporting failure. This is cheap to fix now that there's one table: a Hangfire global `IApplyStateFilter` marks the corresponding `BackgroundJobRun` `Failed` when a job transitions to Hangfire's `FailedState` after exhausting `[AutomaticRetry(Attempts = 3)]`, so `BackgroundJobStatusPoller`/`OnRunCompleted` can surface an actual failure instead of the run quietly going stale. The filter correlates a failing Hangfire job back to its `BackgroundJobRun` row by reading `runId` out of `ElectStateContext.BackgroundJob.Job.Args` — this works generically across all five jobs only because each one's `RunAsync(int runId)` takes the run id as its sole argument, per the `Enqueue(int runId)` convention in #6. `DocumentExtractionJob` already sets `ExtractionStatus.Failed` itself for content-level failures (e.g. malformed PDF) inside its own `catch` — that behavior is unchanged; the new filter only covers the case where Hangfire gives up after transient-failure retries are exhausted, which today isn't reported at all, and it doesn't apply to `DocumentExtractionJob` since that job has no `BackgroundJobRun` row to mark (enforced by checking the failing job's `Type` against `BackgroundJobRunBase`, not merely by argument shape — `DocumentExtractionJob.RunAsync(int documentId)` has the same single-`int`-arg shape as every `BackgroundJobRunBase` job's `RunAsync(int runId)`, and `Document.Id`/`BackgroundJobRun.Id` are independent sequences that can collide, so shape alone isn't a safe correlation key).

## Testing

`BackgroundJobRunBase` and `BackgroundJobStatusPoller` each get their own focused tests once (tenant-bootstrap correctness, progress/complete persistence, timer/staleness/acknowledge behavior). Each concrete job (`SieImportJob`, etc.) and each page's polling wire-up then only needs tests for its own business logic and its own `OnRunCompleted` rendering — not a re-test of the shared timer or context-bootstrap machinery, the way `ZipImportJobTests`/`ZipImportJobRetryStrategyTests` currently have to exercise the full stack themselves.

## Sequencing

1. Land `BackgroundJobRun`/`BackgroundJobType`/`BackgroundJobStatus`, the tenant-bootstrap helper, `BackgroundJobRunBase`, `IBackgroundJobRunService`, and `BackgroundJobStatusPoller` as one PR.
2. Retrofit `ZipImportJob`/`ZipImportBatch` (PR #251, unmerged) onto this before it merges — swap `ZipImportBatch` for `BackgroundJobRun`, rebase `ZipImportJob` onto `BackgroundJobRunBase`, replace `Inbox.razor`'s hand-rolled batch-polling block with `<BackgroundJobStatusPoller>`. Avoids merging #251 with a status entity that's immediately migrated away. Note this is real rework on top of a PR that's already fully implemented and tested — but still cheaper than merging first, which would mean a second migration (`ZipImportBatch` → `BackgroundJobRun`) plus a live-data backfill instead of just amending an unmerged branch.
3. `DocumentExtractionJob` is unaffected — it needs no `BackgroundJobRun`, no `BackgroundJobRunBase`, and no tenant-bootstrap helper (see #2), so there is nothing to retrofit.
4. Implement #279 (SIE import), #280 (BAS import), #281 (year-end close), #282 (archive export) on top of the shared infrastructure — each becomes primarily a `RunAsync` body plus a queue interface pair plus a page-specific `OnRunCompleted` handler.
