# Stream Document Uploads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Change `IDocumentStorage`/`DocumentService` upload methods from `byte[]` to `Stream`, and update all four Razor upload call sites to pass the browser's upload stream straight through instead of building an extra `MemoryStream`/`ToArray()` copy first.

**Architecture:** `IDocumentStorage.SaveAsync` and `DocumentService.UploadAsync`/`UploadAndLinkAsync` take `Stream data` instead of `byte[] data`. `DocumentService` reads the stream into a bounded `byte[]` (capped at 10MB, reusing the existing `ReadBoundedAsync` helper already used by `UploadZipAsync`) before validation/storage/extraction — this removes the caller-side buffering in Razor components but `DbDocumentStorage` still materializes a `byte[]` internally to write the `bytea` column (true streaming writes require a separate, larger Postgres Large Objects migration, tracked as a follow-up issue, not part of this plan).

**Tech Stack:** C#/.NET (net10.0), EF Core + Npgsql, Blazor Server, xUnit.

## Global Constraints

- Per-file size limit stays 10MB (`DocumentService.MaxBytes`); zip container stays 50MB — unchanged in this plan.
- Error messages stay in Swedish, exact existing text preserved (e.g. `"Filen är för stor (max 10 MB)."`).
- `IDocumentStorage.LoadAsync`/`DeleteAsync`, `IDocumentExtractor`, `UploadZipAsync`'s internal zip handling, and the Postgres `bytea` column mapping are explicitly unchanged — see `docs/superpowers/specs/2026-07-09-stream-uploads-design.md`.
- Default to no code comments; only add one where the *why* is genuinely non-obvious, and keep it to one line.

---

### Task 1: `IDocumentStorage` + `DbDocumentStorage` + `DocumentService` — Stream-based upload path

This is one task because the interface, its only two implementers (`DbDocumentStorage`, the test `FailingStorage` stub), and every caller (`DocumentService`, all `DocumentServiceTests` call sites) must change together for the solution to compile — there is no way to ship a partial version of this signature change.

**Files:**
- Modify: `src/KoalaBooks.Domain/Interfaces/IDocumentStorage.cs`
- Modify: `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Modify: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`
- Create: `tests/KoalaBooks.Tests/DbDocumentStorageTests.cs`

**Interfaces:**
- Produces: `IDocumentStorage.SaveAsync(int documentId, string contentType, Stream data) : Task<string>` — consumed by `DocumentService` and Task 2's Razor changes.
- Produces: `DocumentService.UploadAsync(string fileName, string contentType, Stream data) : Task<(Document? Doc, string? Error)>` — consumed by Task 2.
- Produces: `DocumentService.UploadAndLinkAsync(string fileName, string contentType, Stream data, DocumentEntityType entityType, int entityId) : Task<(Document? Doc, string? Error)>` — consumed by Task 2.
- Consumes: existing `DocumentService.ReadBoundedAsync(Stream stream, long maxBytes) : Task<(byte[]? Data, bool Oversized)>` (already defined at `DocumentService.cs:292`, unchanged) — reused directly, no new helper needed.

- [ ] **Step 1: Change `IDocumentStorage.SaveAsync` to accept `Stream`**

Edit `src/KoalaBooks.Domain/Interfaces/IDocumentStorage.cs`:

```csharp
namespace KoalaBooks.Domain.Interfaces;

public interface IDocumentStorage
{
    Task<string> SaveAsync(int documentId, string contentType, Stream data);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
```

(Only the `data` parameter type on `SaveAsync` changes, from `byte[]` to `Stream`.)

- [ ] **Step 2: Update `DbDocumentStorage.SaveAsync` to buffer the incoming stream**

Edit `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`:

```csharp
// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Services;

public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    public async Task<string> SaveAsync(int documentId, string contentType, Stream data)
    {
        using var ms = new MemoryStream();
        await data.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var existing = await db.DocumentData.FindAsync(documentId);
        if (existing is not null)
        {
            existing.Data = bytes;
        }
        else
        {
            db.DocumentData.Add(new DocumentData { DocumentId = documentId, Data = bytes });
        }
        await db.SaveChangesAsync();
        return documentId.ToString();
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];
        var row = await db.DocumentData.FindAsync(id);
        return row?.Data ?? [];
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;
        var row = await db.DocumentData.FindAsync(id);
        if (row is not null)
        {
            db.DocumentData.Remove(row);
            await db.SaveChangesAsync();
        }
    }
}
```

- [ ] **Step 3: Write the `DbDocumentStorage` round-trip test (will not compile yet — that's expected)**

Create `tests/KoalaBooks.Tests/DbDocumentStorageTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

public class DbDocumentStorageTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task SaveAsync_AcceptsStreamAndRoundTripsThroughLoadAsync()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 3,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var bytes = new byte[] { 1, 2, 3 };
        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream(bytes));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingDataOnReupload()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var doc = new Document
        {
            OrganisationId = _fx.OrganisationId,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileSize = 1,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        _fx.Db.Documents.Add(doc);
        await _fx.Db.SaveChangesAsync();

        var key = await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream([1]));
        await storage.SaveAsync(doc.Id, "application/pdf", new MemoryStream([9, 9]));

        var loaded = await storage.LoadAsync(key);
        Assert.Equal(new byte[] { 9, 9 }, loaded);
    }
}
```

- [ ] **Step 4: Run the whole suite to confirm the expected build failure**

Run: `dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj 2>&1 | tail -40`
Expected: **build fails** — `DocumentService.UploadAsync`/`UploadAndLinkAsync` still take `byte[]`, and `DocumentServiceTests`' `FailingStorage.SaveAsync` still implements the old `byte[]` signature, so it no longer satisfies `IDocumentStorage`. This confirms Steps 1-3 wired the new interface correctly (the failure is a type mismatch, not a typo) before Step 5 fixes the rest.

- [ ] **Step 5: Update `DocumentService.UploadAsync`, `UploadAndLinkAsync`, and `UploadZipAsync`'s call into `UploadAsync`**

Edit `src/KoalaBooks.Application/Services/DocumentService.cs`, replacing the `UploadAsync` method (currently lines 39-88):

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

        try
        {
            var result = await extractor.ExtractAsync(fileName, contentType, bytes);
            doc.SuggestedType = result.SuggestedType;
            doc.ExtractedDataJson = result.SuggestedType is not null
                ? JsonSerializer.Serialize(result)
                : null;
            doc.DocumentDate = result.InvoiceDate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Extraction failed for {FileName} — upload proceeds without suggestion", fileName);
        }

        await db.SaveChangesAsync();
        return (doc, null);
    }
```

Replace `UploadAndLinkAsync` (currently lines 207-214):

```csharp
    public async Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, Stream data, DocumentEntityType entityType, int entityId)
    {
        var (doc, err) = await UploadAsync(fileName, contentType, data);
        if (doc is null) return (null, err);
        await LinkAsync(doc.Id, entityType, entityId);
        return (doc, null);
    }
```

In `UploadZipAsync`, change the call to `UploadAsync` (currently line 281) from:

```csharp
                var (doc, err) = await UploadAsync(entry.Name, contentType, data);
```

to:

```csharp
                var (doc, err) = await UploadAsync(entry.Name, contentType, new MemoryStream(data));
```

`ReadBoundedAsync` itself (lines 292-306) is unchanged — its signature already matches what `UploadAsync` needs.

- [ ] **Step 6: Update the `FailingStorage` test stub**

Edit `tests/KoalaBooks.Tests/DocumentServiceTests.cs`, replace:

```csharp
file class FailingStorage : IDocumentStorage
{
    public Task<string> SaveAsync(int documentId, string contentType, byte[] data) =>
        throw new InvalidOperationException("simulated storage failure");

    public Task<byte[]> LoadAsync(string storageKey) => Task.FromResult(Array.Empty<byte>());
    public Task DeleteAsync(string storageKey) => Task.CompletedTask;
}
```

with:

```csharp
file class FailingStorage : IDocumentStorage
{
    public Task<string> SaveAsync(int documentId, string contentType, Stream data) =>
        throw new InvalidOperationException("simulated storage failure");

    public Task<byte[]> LoadAsync(string storageKey) => Task.FromResult(Array.Empty<byte>());
    public Task DeleteAsync(string storageKey) => Task.CompletedTask;
}
```

- [ ] **Step 7: Wrap every existing `byte[]` argument to `UploadAsync`/`UploadAndLinkAsync` in `new MemoryStream(...)`**

In `tests/KoalaBooks.Tests/DocumentServiceTests.cs`, apply these exact line replacements (each `old` line is unique in the file, verified against the current source):

| Old line | New line |
|---|---|
| `        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", new byte[] { 1, 2, 3 });` | `        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream(new byte[] { 1, 2, 3 }));` |
| `        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", []);` | `        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", new MemoryStream());` |
| `        var (doc, err) = await svc.UploadAsync("bad.html", "text/html", [1, 2, 3]);` | `        var (doc, err) = await svc.UploadAsync("bad.html", "text/html", new MemoryStream([1, 2, 3]));` |
| `        var (doc, err) = await svc.UploadAsync("big.pdf", "application/pdf", bigData);` | `        var (doc, err) = await svc.UploadAsync("big.pdf", "application/pdf", new MemoryStream(bigData));` |
| `        await svc.UploadAsync("unlinked.pdf", "application/pdf", [1]);` | `        await svc.UploadAsync("unlinked.pdf", "application/pdf", new MemoryStream([1]));` |
| `        var (linked, _) = await svc.UploadAsync("linked.pdf", "application/pdf", [2]);` | `        var (linked, _) = await svc.UploadAsync("linked.pdf", "application/pdf", new MemoryStream([2]));` |
| `        var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", []);` | `        var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", new MemoryStream());` |
| `        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", [1]);` | `        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1]));` |
| `        var (d1, _) = await svc.UploadAsync("a.pdf", "application/pdf", [1]);` | `        var (d1, _) = await svc.UploadAsync("a.pdf", "application/pdf", new MemoryStream([1]));` |
| `        var (d2, _) = await svc.UploadAsync("b.pdf", "application/pdf", [2]);` | `        var (d2, _) = await svc.UploadAsync("b.pdf", "application/pdf", new MemoryStream([2]));` |
| `        var (doc, _) = await svc.UploadAsync("receipt.pdf", "application/pdf", [5]);` | `        var (doc, _) = await svc.UploadAsync("receipt.pdf", "application/pdf", new MemoryStream([5]));` |
| `        var (doc, _) = await svc.UploadAsync("todelete.pdf", "application/pdf", [9, 8, 7]);` | `        var (doc, _) = await svc.UploadAsync("todelete.pdf", "application/pdf", new MemoryStream([9, 8, 7]));` |
| `        var (doc, _) = await svc.UploadAsync("file.pdf", "application/pdf", [10, 20, 30]);` | `        var (doc, _) = await svc.UploadAsync("file.pdf", "application/pdf", new MemoryStream([10, 20, 30]));` |
| `        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", [1, 2, 3]);` | `        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));` |
| `        var (doc, err) = await svc.UploadAsync("photo.jpg", "image/jpg", [1, 2, 3]);` | `        var (doc, err) = await svc.UploadAsync("photo.jpg", "image/jpg", new MemoryStream([1, 2, 3]));` |
| `        var (doc, _) = await docSvc.UploadAsync("faktura.pdf", "application/pdf", [1]);` | `        var (doc, _) = await docSvc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1]));` |
| `        var (doc1, _) = await svc.UploadAsync("a.pdf", "application/pdf", [1]);` | `        var (doc1, _) = await svc.UploadAsync("a.pdf", "application/pdf", new MemoryStream([1]));` |
| `        var (doc2, _) = await svc.UploadAsync("b.pdf", "application/pdf", [2]);` | `        var (doc2, _) = await svc.UploadAsync("b.pdf", "application/pdf", new MemoryStream([2]));` |
| `        var (doc3, _) = await svc.UploadAsync("c.pdf", "application/pdf", [3]);` | `        var (doc3, _) = await svc.UploadAsync("c.pdf", "application/pdf", new MemoryStream([3]));` |

After editing, verify nothing was missed:

Run: `grep -n 'UploadAsync(".*", "[^"]*", \[' tests/KoalaBooks.Tests/DocumentServiceTests.cs`
Expected: no output (every bracket-literal third argument has been wrapped).

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj 2>&1 | tail -40`
Expected: build succeeds, all tests pass including the two new `DbDocumentStorageTests` and every `DocumentServiceTests`/`UploadZipAsync` test unchanged in behavior.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/IDocumentStorage.cs \
        src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs \
        src/KoalaBooks.Application/Services/DocumentService.cs \
        tests/KoalaBooks.Tests/DocumentServiceTests.cs \
        tests/KoalaBooks.Tests/DbDocumentStorageTests.cs
git commit -m "Change IDocumentStorage/DocumentService upload methods to accept Stream instead of byte[]"
```

---

### Task 2: Razor call sites — pass the browser upload stream straight through

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`
- Modify: `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`
- Modify: `src/KoalaBooks.Components/Pages/SupplierInvoices.razor`
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`

**Interfaces:**
- Consumes: `DocumentService.UploadAsync(string, string, Stream)` and `DocumentService.UploadAndLinkAsync(string, string, Stream, DocumentEntityType, int)` from Task 1.

- [ ] **Step 1: `Inbox.razor` — split the zip and single-file branches so only the zip branch still buffers**

Edit `src/KoalaBooks.Components/Pages/Inbox.razor`, replace the loop body (currently lines 194-236):

```csharp
            foreach (var file in files)
            {
                const long maxBytes = 10 * 1024 * 1024;
                const long zipMaxBytes = 50 * 1024 * 1024;
                var isZip = Path.GetExtension(file.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase);
                var fileMaxBytes = isZip ? zipMaxBytes : maxBytes;

                if (file.Size > fileMaxBytes)
                {
                    _error = isZip ? $"{file.Name}: för stor (max 50 MB)." : $"{file.Name}: för stor (max 10 MB).";
                    continue;
                }

                if (isZip)
                {
                    using var ms = new MemoryStream();
                    await file.OpenReadStream(fileMaxBytes).CopyToAsync(ms);
                    var (result, zipErr) = await DocumentService.UploadZipAsync(ms.ToArray());
                    if (zipErr is not null)
                    {
                        _error = $"{file.Name}: {zipErr}";
                    }
                    else
                    {
                        var summary = $"{file.Name}: {result!.Imported.Count} dokument importerade";
                        if (result.Skipped.Count > 0)
                        {
                            var reasons = string.Join(", ", result.Skipped.Select(s => $"{s.FileName}: {s.Reason}"));
                            summary += $", {result.Skipped.Count} hoppade över ({reasons})";
                        }
                        Snackbar.Add(summary, result.Skipped.Count > 0 ? Severity.Warning : Severity.Success);
                    }
                    continue;
                }

                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
                await using var stream = file.OpenReadStream(fileMaxBytes);
                var (_, err) = await DocumentService.UploadAsync(file.Name, contentType, stream);
                if (err is not null)
                    _error = err;
                else
                    Snackbar.Add($"{file.Name} uppladdad.", Severity.Success);
            }
```

- [ ] **Step 2: `CustomerInvoices.razor` — drop the `MemoryStream` buffer**

Edit `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`, replace (currently lines 682-687):

```csharp
            using var ms = new MemoryStream();
            await e.File.OpenReadStream(maxBytes).CopyToAsync(ms);
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, ms.ToArray(),
                DocumentEntityType.CustomerInvoice, _docPanelInvoiceId!.Value);
```

with:

```csharp
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            await using var stream = e.File.OpenReadStream(maxBytes);
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, stream,
                DocumentEntityType.CustomerInvoice, _docPanelInvoiceId!.Value);
```

- [ ] **Step 3: `SupplierInvoices.razor` — drop the `MemoryStream` buffer**

Edit `src/KoalaBooks.Components/Pages/SupplierInvoices.razor`, replace (currently lines 652-657):

```csharp
            using var ms = new MemoryStream();
            await e.File.OpenReadStream(maxBytes).CopyToAsync(ms);
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, ms.ToArray(),
                DocumentEntityType.SupplierInvoice, _docPanelInvoiceId!.Value);
```

with:

```csharp
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            await using var stream = e.File.OpenReadStream(maxBytes);
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, stream,
                DocumentEntityType.SupplierInvoice, _docPanelInvoiceId!.Value);
```

- [ ] **Step 4: `Journal.razor` — drop the `MemoryStream` buffer**

Edit `src/KoalaBooks.Components/Pages/Journal.razor`, replace (currently lines 597-602):

```csharp
            using var ms = new MemoryStream();
            await e.File.OpenReadStream(maxBytes).CopyToAsync(ms);
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            var (added, uploadErr) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, ms.ToArray(),
                DocumentEntityType.JournalEntry, _attachmentEntryId!.Value);
```

with:

```csharp
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            await using var stream = e.File.OpenReadStream(maxBytes);
            var (added, uploadErr) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, stream,
                DocumentEntityType.JournalEntry, _attachmentEntryId!.Value);
```

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build KoalaBooks.sln 2>&1 | tail -30`
Expected: build succeeds, no warnings about unused `MemoryStream`/`ms` variables.

- [ ] **Step 6: Run the full test suite again**

Run: `dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj 2>&1 | tail -40`
Expected: all tests still pass (this task doesn't touch any tested backend logic, but confirms the build-wide change didn't break anything).

- [ ] **Step 7: Manually verify in the browser**

Start the app (use the project's `run` skill or `dotnet run --project src/KoalaBooks.Web`), then:
1. Go to `/inbox`, upload a single PDF — confirm it appears in the inbox list with the correct file size and, if it's a supplier/customer invoice PDF, a suggested type.
2. On the same page, upload a `.zip` containing 2-3 files — confirm the summary Snackbar still reports the right import count (this path is unchanged, just confirming the split in Step 1 didn't break it).
3. Go to a supplier invoice (or customer invoice) detail view, use its document-attach control to upload a PDF — confirm it attaches and appears in the linked documents list.

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Inbox.razor \
        src/KoalaBooks.Components/Pages/CustomerInvoices.razor \
        src/KoalaBooks.Components/Pages/SupplierInvoices.razor \
        src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "Pass browser upload stream directly to DocumentService instead of buffering into byte[] first"
```

---

## Follow-up (not part of this plan)

Open a new GitHub issue after this plan lands: migrate `DbDocumentStorage` from the `bytea` column to Postgres Large Objects (`NpgsqlLargeObjectManager`) for genuine chunked streaming writes — the only remaining buffer after this plan is `DbDocumentStorage.SaveAsync`'s internal `CopyToAsync(ms)`. Keep it separate from #208 (background extraction) and #209 (background job library), per the design spec.
