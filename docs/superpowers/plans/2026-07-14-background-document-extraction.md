# Background Document Extraction (#208) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `IDocumentExtractor.ExtractAsync` off `DocumentService.UploadAsync`'s synchronous request path onto a Hangfire background job, with `Document.ExtractionStatus` (Pending/Completed/Failed) tracking progress and `Inbox.razor` reflecting it.

**Architecture:** `DocumentService.UploadAsync` sets `ExtractionStatus = Pending` and enqueues a `DocumentExtractionJob` via a new `IDocumentExtractionQueue` abstraction instead of calling the extractor inline. The job (in `KoalaBooks.Application.Jobs`, resolved by Hangfire's ASP.NET Core DI activator) loads the document with `IgnoreQueryFilters()` (no `HttpContext` exists in a background job, so the tenant query filter would otherwise hide every row), re-extracts, and writes the result plus a terminal `ExtractionStatus`. `Inbox.razor` shows a "Bearbetar…" badge and polls while any row on the page is `Pending`.

**Tech Stack:** ASP.NET Core / Blazor Server, EF Core (Npgsql), Hangfire (already wired to Postgres storage per #209, see `src/KoalaBooks.Web/Program.cs:47-55`), xUnit + a real Postgres test container (`TestFixture`/`PostgresContainerFixture`).

## Global Constraints

- Enum values are explicit ints, matching the codebase's existing enum style (e.g. `JournalEntryStatus`): `Pending = 0, Completed = 1, Failed = 2`.
- The `ExtractionStatus` column gets a schema-level default of `Pending` (0), **not** `Completed` — a future code path that forgets to set it should fail loudly (stuck "processing" badge), not silently look done. Only pre-existing rows are backfilled to `Completed`, via an explicit one-time `migrationBuilder.Sql(...)` statement in the same migration, not a schema default.
- `[AutomaticRetry(Attempts = 3)]` on `DocumentExtractionJob.RunAsync` covers only storage/DB read-back failures (left to bubble). `IDocumentExtractor.ExtractAsync` throwing is caught inside the method itself, sets `ExtractionStatus = Failed`, and does not rethrow — retrying a parse failure on the same file is pointless.
- The job loads its document via `db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == documentId)` — safe specifically because the job only ever acts on a `documentId` handed to it by trusted code that just created that exact row, not arbitrary input.
- `HangfireDocumentExtractionQueue`, `NoOpDocumentExtractionQueue`, and `DocumentExtractionJob` all live in `KoalaBooks.Application/Jobs/` (**not** `KoalaBooks.Infrastructure` as the design doc originally sketched — `Infrastructure` has no project reference to `Application`, so a class there referencing `DocumentExtractionJob` would be circular). Only `KoalaBooks.Application.csproj` needs a new `Hangfire.Core` package reference (version `1.8.23`, matching `Hangfire.AspNetCore` already in `Web`); `Infrastructure` needs no Hangfire reference at all.
- Design doc: `docs/superpowers/specs/2026-07-13-background-document-extraction-design.md` (this plan supersedes it on the file-location point above; everything else matches).

---

## Task 1: `ExtractionStatus` enum, `Document.ExtractionStatus`, `IDocumentExtractionQueue`

**Files:**
- Create: `src/KoalaBooks.Domain/Enums/ExtractionStatus.cs`
- Create: `src/KoalaBooks.Domain/Interfaces/IDocumentExtractionQueue.cs`
- Modify: `src/KoalaBooks.Domain/Entities/Document.cs`
- Test: `tests/KoalaBooks.Tests/DocumentServiceTests.cs` (one new assertion, see Task 6 — no dedicated test file needed for a plain default-value check; folded into Task 6's new upload test)

**Interfaces:**
- Produces: `KoalaBooks.Domain.Enums.ExtractionStatus { Pending = 0, Completed = 1, Failed = 2 }`
- Produces: `KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue { void Enqueue(int documentId); }`
- Produces: `Document.ExtractionStatus` property, default `ExtractionStatus.Pending`

- [ ] **Step 1: Create the enum**

```csharp
// src/KoalaBooks.Domain/Enums/ExtractionStatus.cs
namespace KoalaBooks.Domain.Enums;

public enum ExtractionStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}
```

- [ ] **Step 2: Create the queue interface**

```csharp
// src/KoalaBooks.Domain/Interfaces/IDocumentExtractionQueue.cs
namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentExtractionQueue
{
    void Enqueue(int documentId);
}
```

- [ ] **Step 3: Add `ExtractionStatus` to `Document`**

Modify `src/KoalaBooks.Domain/Entities/Document.cs` — add the using and the property (matches the existing `JournalEntry.Status` pattern of `using KoalaBooks.Domain.Enums;` + a defaulted enum property):

```csharp
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Entities;

public class Document
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string StorageKey { get; set; } = "";

    public string? SuggestedType { get; set; }
    public string? ExtractedDataJson { get; set; }
    public string? ClassifiedType { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public ExtractionStatus ExtractionStatus { get; set; } = ExtractionStatus.Pending;

    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<SupplierInvoice> SupplierInvoices { get; set; } = [];
    public List<CustomerInvoice> CustomerInvoices { get; set; } = [];
}
```

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build src/KoalaBooks.Domain/KoalaBooks.Domain.csproj`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Enums/ExtractionStatus.cs \
        src/KoalaBooks.Domain/Interfaces/IDocumentExtractionQueue.cs \
        src/KoalaBooks.Domain/Entities/Document.cs
git commit -m "feat: add ExtractionStatus enum and IDocumentExtractionQueue interface"
```

---

## Task 2: EF Core migration for `ExtractionStatus`

**Files:**
- Create (auto-generated then hand-edited): `src/KoalaBooks.Infrastructure/Migrations/<timestamp>_AddDocumentExtractionStatus.cs`
- Modify (auto-generated): `src/KoalaBooks.Infrastructure/Migrations/AppDbContextModelSnapshot.cs`

**Interfaces:**
- Consumes: `Document.ExtractionStatus` (Task 1)
- Produces: `Documents.ExtractionStatus` integer column in Postgres, default `0`, with every pre-existing row backfilled to `1`.

- [ ] **Step 1: Generate the migration**

```bash
dotnet ef migrations add AddDocumentExtractionStatus \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new files appear under `src/KoalaBooks.Infrastructure/Migrations/`, and `AppDbContextModelSnapshot.cs` is updated. The generated `Up()` should contain exactly one `AddColumn<int>("ExtractionStatus", "Documents", ..., defaultValue: 0)`.

- [ ] **Step 2: Hand-edit the generated migration to add the one-time backfill**

Open the new `*_AddDocumentExtractionStatus.cs` and add the `Sql(...)` call after the `AddColumn`:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<int>(
        name: "ExtractionStatus",
        table: "Documents",
        type: "integer",
        nullable: false,
        defaultValue: 0);

    // One-time backfill: every document that existed before this column was added
    // already ran through the old synchronous extraction path in full (successfully
    // or not) — mark it Completed. New rows explicitly set Pending in code; the
    // column's schema default of 0 (Pending) above is only a safety net for future
    // code paths that forget to set it, not a mechanism for this backfill.
    migrationBuilder.Sql("UPDATE \"Documents\" SET \"ExtractionStatus\" = 1");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropColumn(
        name: "ExtractionStatus",
        table: "Documents");
}
```

- [ ] **Step 3: Build to confirm the migration compiles**

Run: `dotnet build src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Migrations/
git commit -m "feat: add ExtractionStatus column migration with historical-row backfill"
```

---

## Task 3: `DocumentExtractionJob`

**Files:**
- Modify: `src/KoalaBooks.Application/KoalaBooks.Application.csproj`
- Create: `src/KoalaBooks.Application/Jobs/DocumentExtractionJob.cs`
- Test: `tests/KoalaBooks.Tests/DocumentExtractionJobTests.cs`

**Interfaces:**
- Consumes: `AppDbContext` (`KoalaBooks.Infrastructure.Data`), `IDocumentStorage.LoadAsync(string storageKey) : Task<byte[]>`, `IDocumentExtractor.ExtractAsync(string fileName, string contentType, byte[] data) : Task<ExtractionResult>`, `Document.ExtractionStatus` (Task 1)
- Produces: `KoalaBooks.Application.Jobs.DocumentExtractionJob(AppDbContext db, IDocumentStorage storage, IDocumentExtractor extractor, ILogger<DocumentExtractionJob> logger)` with `Task RunAsync(int documentId)`

- [ ] **Step 1: Add the Hangfire.Core package reference**

Modify `src/KoalaBooks.Application/KoalaBooks.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\KoalaBooks.Domain\KoalaBooks.Domain.csproj" />
    <ProjectReference Include="..\KoalaBooks.Infrastructure\KoalaBooks.Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Hangfire.Core" Version="1.8.23" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/KoalaBooks.Tests/DocumentExtractionJobTests.cs
using KoalaBooks.Application.Jobs;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoalaBooks.Tests;

public class DocumentExtractionJobTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task RunAsync_SetsSuggestedTypeAndMarksCompleted()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

        var extractor = new StubExtractor(new ExtractionResult(
            "SupplierInvoice", "ACME AB", 1000m, 250m, new DateOnly(2026, 3, 15), null, "INV-001"));
        var job = new DocumentExtractionJob(_fx.Db, storage, extractor, NullLogger<DocumentExtractionJob>.Instance);

        await job.RunAsync(doc!.Id);

        var updated = await _fx.Db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == doc.Id);
        Assert.Equal("SupplierInvoice", updated.SuggestedType);
        Assert.Equal(new DateOnly(2026, 3, 15), updated.DocumentDate);
        Assert.NotNull(updated.ExtractedDataJson);
        Assert.Equal(ExtractionStatus.Completed, updated.ExtractionStatus);
    }

    [Fact]
    public async Task RunAsync_ExtractorThrows_MarksFailed_DoesNotThrow()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

        var job = new DocumentExtractionJob(_fx.Db, storage, new ThrowingExtractor(), NullLogger<DocumentExtractionJob>.Instance);

        await job.RunAsync(doc!.Id);

        var updated = await _fx.Db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == doc.Id);
        Assert.Equal(ExtractionStatus.Failed, updated.ExtractionStatus);
        Assert.Null(updated.SuggestedType);
    }

    [Fact]
    public async Task RunAsync_UnknownDocumentId_NoOpsWithoutThrowing()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var extractor = new StubExtractor(new ExtractionResult(null, null, null, null, null, null, null));
        var job = new DocumentExtractionJob(_fx.Db, storage, extractor, NullLogger<DocumentExtractionJob>.Instance);

        await job.RunAsync(999_999);
    }

    [Fact]
    public async Task RunAsync_FilenameBasedSuggestion_UsesRealExtractor()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", new MemoryStream());

        var extractor = new CompositeExtractor(new FilenameExtractor(), new PdfTextExtractor(NullLogger<PdfTextExtractor>.Instance));
        var job = new DocumentExtractionJob(_fx.Db, storage, extractor, NullLogger<DocumentExtractionJob>.Instance);

        await job.RunAsync(doc!.Id);

        var updated = await _fx.Db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == doc.Id);
        Assert.Equal("SupplierInvoice", updated.SuggestedType);
        Assert.Null(updated.ClassifiedType);
        Assert.Equal(ExtractionStatus.Completed, updated.ExtractionStatus);
    }
}

file class StubExtractor(ExtractionResult result) : IDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data) =>
        Task.FromResult(result);
}

file class ThrowingExtractor : IDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data) =>
        throw new InvalidOperationException("simulated extraction failure");
}
```

Note: this references `_fx.MakeDocumentService(storage)`, which after Task 6 no longer takes/needs an extractor — it already compiles against the *current* signature `MakeDocumentService(IDocumentStorage storage)`, unchanged by this task. `DocumentExtractionJob` doesn't exist yet, so this won't compile until Step 3.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter DocumentExtractionJobTests`
Expected: build error — `DocumentExtractionJob` does not exist.

- [ ] **Step 4: Implement `DocumentExtractionJob`**

```csharp
// src/KoalaBooks.Application/Jobs/DocumentExtractionJob.cs
using Hangfire;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace KoalaBooks.Application.Jobs;

public class DocumentExtractionJob(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractor extractor,
    ILogger<DocumentExtractionJob> logger)
{
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
            doc.ExtractedDataJson = result.SuggestedType is not null
                ? JsonSerializer.Serialize(result)
                : null;
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
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter DocumentExtractionJobTests`
Expected: 4 passed.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/KoalaBooks.Application.csproj \
        src/KoalaBooks.Application/Jobs/DocumentExtractionJob.cs \
        tests/KoalaBooks.Tests/DocumentExtractionJobTests.cs
git commit -m "feat: add DocumentExtractionJob"
```

---

## Task 4: `HangfireDocumentExtractionQueue` and `NoOpDocumentExtractionQueue`

**Files:**
- Create: `src/KoalaBooks.Application/Jobs/HangfireDocumentExtractionQueue.cs`
- Create: `src/KoalaBooks.Application/Jobs/NoOpDocumentExtractionQueue.cs`

**Interfaces:**
- Consumes: `DocumentExtractionJob.RunAsync(int documentId)` (Task 3), `IDocumentExtractionQueue` (Task 1), Hangfire's `IBackgroundJobClient` (from `Hangfire.Core`, already referenced per Task 3)
- Produces: `KoalaBooks.Application.Jobs.HangfireDocumentExtractionQueue(IBackgroundJobClient jobClient) : IDocumentExtractionQueue`, `KoalaBooks.Application.Jobs.NoOpDocumentExtractionQueue : IDocumentExtractionQueue`

No dedicated unit tests for these two: `HangfireDocumentExtractionQueue` is a two-line wrapper around a well-tested third-party client (`IBackgroundJobClient`), and `NoOpDocumentExtractionQueue` has no branching logic. Both get exercised implicitly — `NoOpDocumentExtractionQueue` via the `Testing`-environment DI wiring in Task 5, `HangfireDocumentExtractionQueue`'s wiring via manual verification in Task 8 (the `/hangfire` dashboard shows the enqueued job after a real upload).

- [ ] **Step 1: Implement `HangfireDocumentExtractionQueue`**

```csharp
// src/KoalaBooks.Application/Jobs/HangfireDocumentExtractionQueue.cs
using Hangfire;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class HangfireDocumentExtractionQueue(IBackgroundJobClient jobClient) : IDocumentExtractionQueue
{
    public void Enqueue(int documentId) =>
        jobClient.Enqueue<DocumentExtractionJob>(job => job.RunAsync(documentId));
}
```

- [ ] **Step 2: Implement `NoOpDocumentExtractionQueue`**

```csharp
// src/KoalaBooks.Application/Jobs/NoOpDocumentExtractionQueue.cs
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Application.Jobs;

public class NoOpDocumentExtractionQueue : IDocumentExtractionQueue
{
    public void Enqueue(int documentId) { }
}
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/KoalaBooks.Application/KoalaBooks.Application.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Application/Jobs/HangfireDocumentExtractionQueue.cs \
        src/KoalaBooks.Application/Jobs/NoOpDocumentExtractionQueue.cs
git commit -m "feat: add Hangfire-backed and no-op IDocumentExtractionQueue implementations"
```

---

## Task 5: Wire `IDocumentExtractionQueue` and `DocumentExtractionJob` into `Program.cs`

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs:47-55` (Hangfire block), `src/KoalaBooks.Web/Program.cs:146-151` (document services block)

**Interfaces:**
- Consumes: `HangfireDocumentExtractionQueue`, `NoOpDocumentExtractionQueue`, `DocumentExtractionJob` (Tasks 3-4)
- Produces: `IDocumentExtractionQueue` resolvable from DI in every environment (Hangfire-backed outside `Testing`, no-op inside it), `DocumentExtractionJob` resolvable from DI so Hangfire's `AspNetCoreJobActivator` can construct it per-invocation with its constructor dependencies.

- [ ] **Step 1: Register the queue and the job in the existing Hangfire block**

Modify `src/KoalaBooks.Web/Program.cs:45-55` from:

```csharp
// Excluded from Testing: eager Postgres schema-prep here corrupts EnsureCreated()'s
// schema visibility under WebApplicationFactory (and exhausts connections under load).
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(koalabooksConnectionString)));
    builder.Services.AddHangfireServer();
}
```

to:

```csharp
// Excluded from Testing: eager Postgres schema-prep here corrupts EnsureCreated()'s
// schema visibility under WebApplicationFactory (and exhausts connections under load).
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(koalabooksConnectionString)));
    builder.Services.AddHangfireServer();
    builder.Services.AddScoped<KoalaBooks.Application.Jobs.DocumentExtractionJob>();
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue,
        KoalaBooks.Application.Jobs.HangfireDocumentExtractionQueue>();
}
else
{
    builder.Services.AddScoped<KoalaBooks.Domain.Interfaces.IDocumentExtractionQueue,
        KoalaBooks.Application.Jobs.NoOpDocumentExtractionQueue>();
}
```

(Fully-qualified names match the existing style used for `IDocumentExtractor`/`IDocumentStorage` a few lines below at `Program.cs:146-149`, rather than adding new `using` directives.)

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj`
Expected: `Build succeeded.` (`DocumentService` still requires `IDocumentExtractor` at this point since Task 6 hasn't run yet — that registration at `Program.cs:146-147` is untouched and still resolves fine.)

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Web/Program.cs
git commit -m "feat: register IDocumentExtractionQueue (Hangfire-backed outside Testing)"
```

---

## Task 6: `DocumentService.UploadAsync` enqueues instead of extracting inline

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs:188-199`
- Modify: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: `IDocumentExtractionQueue.Enqueue(int documentId)` (Task 1), `Document.ExtractionStatus` (Task 1)
- Produces: `DocumentService` constructor becomes `(AppDbContext db, IDocumentStorage storage, IDocumentExtractionQueue extractionQueue, ICurrentUser currentUser)` (drops `IDocumentExtractor` and the now-unused `ILogger<DocumentService>`); `DocumentMeta.ExtractionStatus` field.

- [ ] **Step 1: Write the failing tests**

In `tests/KoalaBooks.Tests/DocumentServiceTests.cs`:

1. Replace the test at lines 27-35 (`UploadAsync_SetsSuggestedTypeFromFilename_ClassifiedTypeRemainsNull`) — extraction no longer runs synchronously, so this exact assertion is now false. Replace it with:

```csharp
    [Fact]
    public async Task UploadAsync_SetsExtractionStatusPending_NoSuggestionYet()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", new MemoryStream());

        Assert.Equal(ExtractionStatus.Pending, doc!.ExtractionStatus);
        Assert.Null(doc.SuggestedType);
        Assert.Null(doc.ClassifiedType);
    }
```

2. Delete the test at lines 92-103 (`UploadAsync_PopulatesDocumentDateFromExtractor`) entirely — equivalent coverage now lives in `DocumentExtractionJobTests.RunAsync_SetsSuggestedTypeAndMarksCompleted` (Task 3).

3. Add a new test asserting the enqueue call, placed near the other `UploadAsync_*` tests:

```csharp
    [Fact]
    public async Task UploadAsync_EnqueuesExtractionJob()
    {
        var queue = new RecordingExtractionQueue();
        var svc = _fx.MakeDocumentService(queue);

        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

        Assert.Equal(doc!.Id, Assert.Single(queue.EnqueuedDocumentIds));
    }
```

4. Remove the `file class StubExtractor` block at the bottom of the file (lines 446-450) — no longer used here (it now lives, separately, in `DocumentExtractionJobTests.cs` from Task 3). Add a new `file class RecordingExtractionQueue` in its place:

```csharp
file class RecordingExtractionQueue : IDocumentExtractionQueue
{
    public List<int> EnqueuedDocumentIds { get; } = [];
    public void Enqueue(int documentId) => EnqueuedDocumentIds.Add(documentId);
}
```

In `tests/KoalaBooks.Tests/TestFixture.cs`, replace lines 188-199:

```csharp
    public DocumentService MakeDocumentService() =>
        MakeDocumentService(new DbDocumentStorage(Db));

    public DocumentService MakeDocumentService(IDocumentStorage storage)
    {
        var extractor = new CompositeExtractor(new FilenameExtractor(), new PdfTextExtractor(
            NullLogger<PdfTextExtractor>.Instance));
        return new DocumentService(Db, storage, extractor, _currentUser, NullLogger<DocumentService>.Instance);
    }

    public DocumentService MakeDocumentService(IDocumentExtractor extractor) =>
        new DocumentService(Db, new DbDocumentStorage(Db), extractor, _currentUser,
            NullLogger<DocumentService>.Instance);
```

with (note: `DocumentService`'s constructor drops `ILogger<DocumentService>` entirely in Step 3 below — it was only ever used in the extraction catch block being removed — so these factory methods don't pass one):

```csharp
    public DocumentService MakeDocumentService() =>
        MakeDocumentService(new DbDocumentStorage(Db));

    public DocumentService MakeDocumentService(IDocumentStorage storage) =>
        new DocumentService(Db, storage, new NoOpDocumentExtractionQueue(), _currentUser);

    public DocumentService MakeDocumentService(IDocumentExtractionQueue extractionQueue) =>
        new DocumentService(Db, new DbDocumentStorage(Db), extractionQueue, _currentUser);
```

Add `using KoalaBooks.Application.Jobs;` to `TestFixture.cs`'s using block (needed for `NoOpDocumentExtractionQueue`). The `CompositeExtractor`/`FilenameExtractor`/`PdfTextExtractor` construction moves out of `TestFixture` entirely — those are only needed directly in `DocumentExtractionJobTests.cs` now (Task 3 already constructs them there). `KoalaBooks.Infrastructure.Services` stays in the using list regardless, since `DbDocumentStorage` (still used) lives there.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter DocumentServiceTests`
Expected: compile errors — `DocumentService` constructor still takes `IDocumentExtractor`, `Document.ExtractionStatus` reference is fine (Task 1) but `MakeDocumentService(IDocumentExtractionQueue)` doesn't exist yet, and `RecordingExtractionQueue`/removed `StubExtractor` mismatch.

- [ ] **Step 3: Update `DocumentService.cs`**

Modify `src/KoalaBooks.Application/Services/DocumentService.cs`. Replace `UploadAsync` (lines 39-90) — drop the second `try`/`catch` block (extraction) for a status set + enqueue:

```csharp
    public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, Stream data)
    {
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");
        if (!AllowedContentTypes.Contains(contentType))
            return (null, "Otillåten filtyp. Tillåtna typer: PDF, PNG, JPEG.");

        var (bytes, oversized) = await ReadBoundedAsync(data, MaxBytes);
        if (oversized)
            return (null, "Filen är för stor (max 10 MB).");

        var doc = new Document
        {
            OrganisationId = currentUser.OrganisationId.Value,
            FileName = fileName,
            ContentType = contentType,
            FileSize = bytes!.Length,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(); // gets doc.Id

        try
        {
            doc.StorageKey = await storage.SaveAsync(doc.Id, contentType, new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            // Storage failed — roll back the DB row to avoid orphaned metadata
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

`System.Text.Json` (only used for `JsonSerializer.Serialize(result)` in the removed block) and `Microsoft.Extensions.Logging` (only used by the `logger` parameter, also removed) both become unused — drop both usings, and drop `ILogger<DocumentService> logger` from the constructor. Full updated top-of-file:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;

namespace KoalaBooks.Application.Services;

public class DocumentService(
    AppDbContext db,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    ICurrentUser currentUser)
{
```

`TestFixture.cs`'s `MakeDocumentService` bodies were already written without a logger argument in Step 1 above — no further change needed there.

No changes needed in `Program.cs`: `Program.cs:150` (`builder.Services.AddScoped<DocumentService>();`) resolves fine regardless of constructor parameter count, and `Program.cs:143-147` (`IDocumentExtractor`/`CompositeExtractor` registrations) must stay exactly as-is — `DocumentService` no longer needs `IDocumentExtractor`, but `DocumentExtractionJob` (registered in Task 5) does, and it's resolved from the same `IServiceProvider`.

Add `ExtractionStatus` to `DocumentMeta` and its mapping in `SelectMetaAsync` (near the end of `DocumentService.cs`):

```csharp
public class DocumentMeta
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? ClassifiedType { get; set; }
    public string? SuggestedType { get; set; }
    public string? ExtractedDataJson { get; set; }
    public DateOnly? DocumentDate { get; set; }
    public ExtractionStatus ExtractionStatus { get; set; }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:N1} KB",
        _ => $"{FileSize / (1024.0 * 1024):N1} MB"
    };
}
```

```csharp
    private static Task<List<DocumentMeta>> SelectMetaAsync(IQueryable<Document> query) =>
        query.Select(d => new DocumentMeta
        {
            Id = d.Id,
            FileName = d.FileName,
            ContentType = d.ContentType,
            FileSize = d.FileSize,
            UploadedAt = d.UploadedAt,
            ClassifiedType = d.ClassifiedType,
            SuggestedType = d.SuggestedType,
            ExtractedDataJson = d.ExtractedDataJson,
            DocumentDate = d.DocumentDate,
            ExtractionStatus = d.ExtractionStatus
        }).ToListAsync();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "DocumentServiceTests|DocumentExtractionJobTests"`
Expected: all pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all pass — this also exercises `WebApiFactory`-based tests (`ApiTests.cs`, `DevelopmentStartupTests.cs`) and `TenantIsolationTests.cs`, confirming `DocumentService`'s new constructor resolves fine under the `Testing` DI graph (Task 5's `NoOpDocumentExtractionQueue` registration).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs \
        tests/KoalaBooks.Tests/TestFixture.cs \
        tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "feat: DocumentService enqueues extraction instead of running it inline"
```

---

## Task 7: `Inbox.razor` — pending badge and polling

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`

**Interfaces:**
- Consumes: `DocumentMeta.ExtractionStatus` (Task 6)

- [ ] **Step 1: Add the `Pending` badge next to the existing classified-type badge**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, modify the `<td>` at lines 80-87:

```razor
                        <td>
                            @if (doc.ExtractionStatus == ExtractionStatus.Pending)
                            {
                                <span style="font-size:0.75rem; padding:2px 8px; border-radius:9999px; background:#fef3c7; color:#92400e;">
                                    Bearbetar…
                                </span>
                            }
                            @if (doc.ClassifiedType is not null)
                            {
                                <span style="font-size:0.75rem; padding:2px 8px; border-radius:9999px; background:#e2e8f0; color:#475569;">
                                    @TypeLabel(doc.ClassifiedType)
                                </span>
                            }
                        </td>
```

- [ ] **Step 2: Add the polling timer**

Modify the `@code` block. First, add `@implements IDisposable` right after the `@page` directive at the top of the file:

```razor
@page "/inbox"
@implements IDisposable
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Enums
@using MudBlazor
@using Microsoft.AspNetCore.Components.Forms
```

Then, in `@code`, add a timer field, update `LoadPageAsync` to accept a `showSpinner` flag and start/stop polling based on whether any row is `Pending`, and add `Dispose`:

```csharp
    private List<DocumentMeta> _docs = [];
    private bool _isLoading;
    private bool _uploading;
    private string? _error;
    private string _filter = "all";
    private string _sortBy = "uploadedAt";
    private bool _sortAsc = false;
    private int _page = 1;
    private int _totalCount;
    private const int PageSize = 50;
    private System.Threading.Timer? _pollTimer;

    private int TotalPages => (_totalCount + PageSize - 1) / PageSize;

    private static (string Label, string Value)[] Filters =>
    [
        ("Alla", "all"),
        ("Oklassificerade", "unclassified"),
        ("Leverantörsfaktura", nameof(DocumentEntityType.SupplierInvoice)),
        ("Kundfaktura", nameof(DocumentEntityType.CustomerInvoice)),
        ("Verifikation", nameof(DocumentEntityType.JournalEntry)),
    ];

    protected override async Task OnInitializedAsync() => await LoadPageAsync();

    private async Task LoadPageAsync(bool showSpinner = true)
    {
        if (showSpinner) _isLoading = true;
        var skip = (_page - 1) * PageSize;
        _docs = await DocumentService.GetPendingAsync(_filter, skip, PageSize, _sortBy, _sortAsc);
        _totalCount = await DocumentService.GetPendingCountAsync(_filter);
        _isLoading = false;
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        var hasPending = _docs.Any(d => d.ExtractionStatus == ExtractionStatus.Pending);
        if (hasPending)
        {
            _pollTimer ??= new System.Threading.Timer(OnPollTick, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        else
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }

    private void OnPollTick(object? state) =>
        _ = InvokeAsync(async () =>
        {
            await LoadPageAsync(showSpinner: false);
            StateHasChanged();
        });

    public void Dispose() => _pollTimer?.Dispose();
```

(`System.Threading.Timer` is fully qualified rather than adding a `@using System.Threading` — avoids any ambiguity with `System.Timers.Timer`, and matches the fully-qualified style already used elsewhere in this codebase for one-off type references.)

Every other call site of `LoadPageAsync()` (`SetFilterAsync`, `PrevPageAsync`, `NextPageAsync`, `SortByAsync`, `OpenClassifyDialogAsync`, `OpenPreviewDialogAsync`, `DeleteDocAsync`) keeps calling `LoadPageAsync()` with no argument — the new `showSpinner` parameter defaults to `true`, so none of those call sites need to change.

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Inbox.razor
git commit -m "feat: show pending-extraction badge and poll Inbox while documents are processing"
```

---

## Task 8: Full-suite verification and a real run-through

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass (no regressions across `KoalaBooks.Tests` or `KoalaBooks.ComponentTests`).

- [ ] **Step 2: Run the `verify` skill against the actual running app**

Use the project's `run`/`verify` workflow to start the app (Postgres + Web, e.g. via `docker compose` per this repo's existing dev setup) and manually drive the Inbox page:
1. Upload a PDF whose filename matches a known supplier-invoice pattern (e.g. `leverantörsfaktura.pdf`) via `/inbox`.
2. Confirm the row appears immediately with a "Bearbetar…" badge and no classified type.
3. Wait up to ~5-10s and confirm the badge disappears on its own (polling picked up the job completing) without a manual page reload.
4. Open "Bokför" on that document and confirm `SuggestedType`/`ExtractedDataJson` populated correctly (same UX as before, just delayed).
5. Visit `/hangfire` (as an Admin-role user) and confirm the `DocumentExtractionJob.RunAsync` job shows as Succeeded.
6. Upload a corrupt/garbage file with a `.pdf` extension and confirm it ends up with no suggestion and no perpetual "Bearbetar…" badge (i.e. `ExtractionStatus` reached `Failed`, not stuck at `Pending`) — check directly via `/hangfire` or the DB if the UI doesn't expose a Failed-specific badge.

Report any deviation from expected behavior before considering this done.

- [ ] **Step 3: Final commit if verification turned up fixes**

If Step 2 required any code changes, commit them separately with a clear message describing what was fixed; otherwise no commit needed for this task.
