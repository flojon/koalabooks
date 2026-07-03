# Zip-File Inbox Upload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users upload a `.zip` file through the existing document-inbox picker; each entry inside is unpacked server-side and run through the same validation/storage/extraction pipeline a single-file upload already uses.

**Architecture:** A new `DocumentService.UploadZipAsync(byte[] zipData)` method opens the zip with `System.IO.Compression.ZipArchive`, applies container-level guards (size, entry count, corrupt file), then loops entries **one at a time** — checking each entry's declared size before reading it, inferring content-type from extension, and delegating to the existing `UploadAsync` for storage/extraction. `Inbox.razor` gets `.zip` added to its accepted extensions and a branch in its existing upload loop; no new UI element.

**Tech Stack:** .NET 10 / C#, EF Core, Blazor Server + MudBlazor, xUnit + Testcontainers.PostgreSql (existing test stack — no new packages).

## Global Constraints

- Zip container max size: 50MB (separate from the existing 10MB single-file limit).
- Max entries per zip: 50.
- Per-entry size: 10MB (reuse existing `DocumentService.MaxBytes`).
- Allowed entry types: pdf, png, jpg/jpeg (reuse existing `DocumentService.AllowedContentTypes`).
- Nested folder paths inside the zip are flattened — only the file name is used.
- One bad entry must not abort the rest of the batch (skip + report, not fatal).
- No streaming — this feature keeps the existing `byte[]`-based buffering pattern. Full streaming is tracked separately as [#187](https://github.com/flojon/koalabooks/issues/187) and is out of scope.
- Reference spec: `docs/superpowers/specs/2026-07-02-zip-inbox-upload-design.md`.

---

## Task 1: `UploadZipAsync` — happy path (import, flatten paths, skip directory entries)

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Test: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: existing `DocumentService.UploadAsync(string fileName, string contentType, byte[] data) : Task<(Document? Doc, string? Error)>` (unchanged, `DocumentService.cs:28`), `TestFixture.MakeDocumentService() : DocumentService` (`tests/KoalaBooks.Tests/TestFixture.cs:186`).
- Produces:
  - `public record ZipImportResult(IReadOnlyList<Document> Imported, IReadOnlyList<(string FileName, string Reason)> Skipped);` — used by later tasks and by `Inbox.razor` (Task 4).
  - `public Task<(ZipImportResult? Result, string? Error)> UploadZipAsync(byte[] zipData)` on `DocumentService` — used by later tasks and by `Inbox.razor` (Task 4).

This task builds the core loop without validation/guard behavior yet (those are Tasks 2 and 3) — it must still produce a correct, independently testable deliverable: given a well-formed small zip, every valid entry is imported, nested paths are flattened, and directory entries are skipped.

- [ ] **Step 1: Write the failing tests**

Add to `tests/KoalaBooks.Tests/DocumentServiceTests.cs`, just above the closing `}` of the `DocumentServiceTests` class (after `GetCountsForJournalEntriesAsync_CountsCorrectly`, before the file-scoped `FailingStorage`/`StubExtractor` classes at the bottom):

```csharp
    [Fact]
    public async Task UploadZipAsync_ImportsAllValidEntries()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));

        var (result, err) = await svc.UploadZipAsync(zip);

        Assert.Null(err);
        Assert.NotNull(result);
        Assert.Equal(2, result.Imported.Count);
        Assert.Contains(result.Imported, d => d.FileName == "a.pdf");
        Assert.Contains(result.Imported, d => d.FileName == "b.png");
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task UploadZipAsync_FlattensNestedFolderPaths()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("invoices/2026/faktura.pdf", new byte[] { 1, 2, 3 }));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("faktura.pdf", result.Imported[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsDirectoryEntries()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZipWithDirectoryEntry();

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("faktura.pdf", result.Imported[0].FileName);
    }

    private static byte[] BuildZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    private static byte[] BuildZipWithDirectoryEntry()
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("empty_folder/");
            var entry = archive.CreateEntry("faktura.pdf");
            using var entryStream = entry.Open();
            var data = new byte[] { 1, 2, 3 };
            entryStream.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~UploadZipAsync"`
Expected: FAIL — build error, `DocumentService` does not contain a definition for `UploadZipAsync`.

- [ ] **Step 3: Implement the minimal core method**

In `src/KoalaBooks.Application/Services/DocumentService.cs`, add near the top of the file (after the existing `using` block, line 7):

```csharp
using System.IO.Compression;
```

Add inside the `DocumentService` class, after the `AllowedContentTypes` field (after line 26):

```csharp
    private static readonly Dictionary<string, string> ZipEntryContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };
```

Add a new public method after `UploadAndLinkAsync` (after line 203, before `SelectMetaAsync`):

```csharp
    public async Task<(ZipImportResult? Result, string? Error)> UploadZipAsync(byte[] zipData)
    {
        using var ms = new MemoryStream(zipData);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var imported = new List<Document>();
        var skipped = new List<(string FileName, string Reason)>();

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // directory entry — ZipArchiveEntry.Name is empty for these

            if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
            {
                skipped.Add((entry.Name, "Otillåten filtyp."));
                continue;
            }

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            await entryStream.CopyToAsync(buffer);

            var (doc, err) = await UploadAsync(entry.Name, contentType, buffer.ToArray());
            if (doc is not null)
                imported.Add(doc);
            else
                skipped.Add((entry.Name, err ?? "Okänt fel."));
        }

        return (new ZipImportResult(imported, skipped), null);
    }
```

Add the record near `DocumentMeta` at the bottom of the file (after the closing `}` of the `DocumentMeta` class, end of file):

```csharp

public record ZipImportResult(IReadOnlyList<Document> Imported, IReadOnlyList<(string FileName, string Reason)> Skipped);
```

Note: `entry.Name` (not `entry.FullName`) already gives just the file-name portion for a real file entry, and is empty for a directory entry — this single property handles both the "flatten nested folders" and "skip directory entries" requirements without extra path parsing.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~UploadZipAsync"`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "feat: add DocumentService.UploadZipAsync core extraction loop"
```

---

## Task 2: `UploadZipAsync` — per-entry size guard and partial-failure reporting

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Test: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: `DocumentService.MaxBytes` (existing private const, `DocumentService.cs:18`), `ZipImportResult` and `UploadZipAsync` from Task 1.
- Produces: no new public surface — behavior refinement of `UploadZipAsync` from Task 1 (declared-size check before reading an entry; a failing `UploadAsync` call for one entry, e.g. simulated storage failure, is recorded in `Skipped` rather than aborting the batch).

- [ ] **Step 1: Write the failing tests**

Add to `tests/KoalaBooks.Tests/DocumentServiceTests.cs`, after the three tests added in Task 1:

```csharp
    [Fact]
    public async Task UploadZipAsync_SkipsInvalidEntriesAndReportsReasons()
    {
        var svc = _fx.MakeDocumentService();
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.exe", new byte[] { 1, 2, 3 }));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("good.pdf", result.Imported[0].FileName);
        Assert.Single(result.Skipped);
        Assert.Equal("bad.exe", result.Skipped[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsOversizedEntry()
    {
        var svc = _fx.MakeDocumentService();
        var bigData = new byte[11 * 1024 * 1024];
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("big.pdf", bigData));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("good.pdf", result.Imported[0].FileName);
        Assert.Single(result.Skipped);
        Assert.Equal("big.pdf", result.Skipped[0].FileName);
    }

    [Fact]
    public async Task UploadZipAsync_SkipsEntryWhenStorageFails_RestOfBatchStillImports()
    {
        var svc = _fx.MakeDocumentService(new FailingStorage());
        var zip = BuildZip(("faktura.pdf", new byte[] { 1, 2, 3 }));

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Empty(result!.Imported);
        Assert.Single(result.Skipped);
        Assert.Equal("faktura.pdf", result.Skipped[0].FileName);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~UploadZipAsync"`
Expected: all three PASS already under Task 1's implementation — `UploadAsync` itself rejects data over `MaxBytes`, so the assertions in `UploadZipAsync_SkipsOversizedEntry` are satisfied even before Step 3's pre-read check exists. These tests pin the *observable* behavior (oversized entries end up in `Skipped`, other entries still import) before Step 3 changes *how* that happens (rejecting via `entry.Length` before ever reading the entry, instead of reading it fully and letting `UploadAsync` reject it). Step 3 is a memory-safety refactor, not new externally-visible behavior — these tests must stay green through it.

- [ ] **Step 3: Add the declared-size check before reading**

In `src/KoalaBooks.Application/Services/DocumentService.cs`, in `UploadZipAsync`, insert a length check between the content-type check and opening the entry stream:

```csharp
            if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
            {
                skipped.Add((entry.Name, "Otillåten filtyp."));
                continue;
            }

            if (entry.Length > MaxBytes)
            {
                skipped.Add((entry.Name, "Filen är för stor (max 10 MB)."));
                continue;
            }

            using var entryStream = entry.Open();
```

(`entry.Length` is the entry's declared uncompressed size, available from the zip's central directory without decompressing — this rejects an oversized entry before any bytes are read into memory.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~UploadZipAsync"`
Expected: PASS (6 tests total across Tasks 1 and 2)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "feat: reject oversized zip entries before reading them"
```

---

## Task 3: `UploadZipAsync` — container-level guards (size, entry count, corrupt file)

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`
- Test: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: `ZipImportResult`, `UploadZipAsync` from Tasks 1-2.
- Produces: no new public surface — `UploadZipAsync` now returns `(null, "<message>")` for three whole-zip rejection cases (oversized container, too many entries, corrupt/invalid zip), instead of only ever returning a populated result.

- [ ] **Step 1: Write the failing tests**

Add to `tests/KoalaBooks.Tests/DocumentServiceTests.cs`, after the tests added in Task 2:

```csharp
    [Fact]
    public async Task UploadZipAsync_RejectsOversizedZipContainer()
    {
        var svc = _fx.MakeDocumentService();
        var bigZip = new byte[51 * 1024 * 1024];

        var (result, err) = await svc.UploadZipAsync(bigZip);

        Assert.Null(result);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsZipWithTooManyEntries()
    {
        var svc = _fx.MakeDocumentService();
        var entries = Enumerable.Range(1, 51)
            .Select(i => ($"file{i}.pdf", new byte[] { 1 }))
            .ToArray();
        var zip = BuildZip(entries);

        var (result, err) = await svc.UploadZipAsync(zip);

        Assert.Null(result);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task UploadZipAsync_RejectsCorruptZipFile()
    {
        var svc = _fx.MakeDocumentService();
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };

        var (result, err) = await svc.UploadZipAsync(corruptBytes);

        Assert.Null(result);
        Assert.NotNull(err);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~UploadZipAsync"`
Expected: FAIL —
- `UploadZipAsync_RejectsOversizedZipContainer`: currently tries to open a 51MB all-zero byte array as a `ZipArchive` and throws `InvalidDataException` unhandled (test errors, doesn't just fail an assert).
- `UploadZipAsync_RejectsZipWithTooManyEntries`: currently succeeds and imports all 51, `result` is not null.
- `UploadZipAsync_RejectsCorruptZipFile`: currently throws `InvalidDataException` unhandled.

- [ ] **Step 3: Add the container-level guards**

In `src/KoalaBooks.Application/Services/DocumentService.cs`, add two new constants next to `MaxBytes` (line 18):

```csharp
    private const long MaxBytes = 10 * 1024 * 1024;
    private const long ZipMaxBytes = 50 * 1024 * 1024;
    private const int ZipMaxEntries = 50;
```

Replace the body of `UploadZipAsync` (from Task 1/2) with:

```csharp
    public async Task<(ZipImportResult? Result, string? Error)> UploadZipAsync(byte[] zipData)
    {
        if (zipData.Length > ZipMaxBytes)
            return (null, "Zip-filen är för stor (max 50 MB).");

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zipData), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return (null, "Ogiltig zip-fil.");
        }

        using (archive)
        {
            var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            if (fileEntries.Count > ZipMaxEntries)
                return (null, $"För många filer i zip-filen (max {ZipMaxEntries}).");

            var imported = new List<Document>();
            var skipped = new List<(string FileName, string Reason)>();

            foreach (var entry in fileEntries)
            {
                if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
                {
                    skipped.Add((entry.Name, "Otillåten filtyp."));
                    continue;
                }

                if (entry.Length > MaxBytes)
                {
                    skipped.Add((entry.Name, "Filen är för stor (max 10 MB)."));
                    continue;
                }

                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                await entryStream.CopyToAsync(buffer);

                var (doc, err) = await UploadAsync(entry.Name, contentType, buffer.ToArray());
                if (doc is not null)
                    imported.Add(doc);
                else
                    skipped.Add((entry.Name, err ?? "Okänt fel."));
            }

            return (new ZipImportResult(imported, skipped), null);
        }
    }
```

(The directory-entry filter that used to be a `continue` inside the loop is now the `fileEntries` `Where` clause, since entry count needs to exclude directories before the `ZipMaxEntries` check.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~UploadZipAsync"`
Expected: PASS (9 tests total across Tasks 1-3)

Then run the full suite to confirm nothing else broke:

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS (all tests, including the pre-existing `DocumentServiceTests`)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs tests/KoalaBooks.Tests/DocumentServiceTests.cs
git commit -m "feat: reject oversized/corrupt/too-large zip containers"
```

---

## Task 4: Wire `.zip` upload into the Inbox UI

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`

**Interfaces:**
- Consumes: `DocumentService.UploadZipAsync(byte[] zipData) : Task<(ZipImportResult? Result, string? Error)>` and `ZipImportResult { IReadOnlyList<Document> Imported, IReadOnlyList<(string FileName, string Reason)> Skipped }` from Tasks 1-3.
- Produces: nothing new consumed elsewhere — this is the UI leaf of the feature.

There is no component-test harness (bUnit or similar) anywhere in this repo's test suite today — `DocumentServiceTests.cs` is the only test file touching document upload, and it tests `DocumentService` directly. Following that existing convention, this task is verified manually (dotnet run + browser), not by an automated test.

- [ ] **Step 1: Update the accepted extensions and helper text**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, replace lines 17-31:

```razor
    <MudFileUpload T="IReadOnlyList<IBrowserFile>" FilesChanged="UploadAsync" Accept=".pdf,.png,.jpg,.jpeg" MaximumFileCount="10">
        <CustomContent>
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudUpload"
                       OnClick="@context.OpenFilePickerAsync"
                       Disabled="@_uploading">
                @(_uploading ? "Laddar upp..." : "Ladda upp dokument")
            </MudButton>
        </CustomContent>
        <SelectedTemplate>
            @* Drag-and-drop zone label — shown when files are dragged over *@
        </SelectedTemplate>
    </MudFileUpload>
    <div style="font-size:0.78rem; color:#94a3b8; margin-top:0.3rem;">
        Dra och släpp filer här eller klicka — PDF, PNG, JPG · max 10 MB per fil · upp till 10 filer
    </div>
```

with:

```razor
    <MudFileUpload T="IReadOnlyList<IBrowserFile>" FilesChanged="UploadAsync" Accept=".pdf,.png,.jpg,.jpeg,.zip" MaximumFileCount="10">
        <CustomContent>
            <MudButton Variant="Variant.Filled" Color="Color.Primary" StartIcon="@Icons.Material.Filled.CloudUpload"
                       OnClick="@context.OpenFilePickerAsync"
                       Disabled="@_uploading">
                @(_uploading ? "Laddar upp..." : "Ladda upp dokument")
            </MudButton>
        </CustomContent>
        <SelectedTemplate>
            @* Drag-and-drop zone label — shown when files are dragged over *@
        </SelectedTemplate>
    </MudFileUpload>
    <div style="font-size:0.78rem; color:#94a3b8; margin-top:0.3rem;">
        Dra och släpp filer här eller klicka — PDF, PNG, JPG eller ZIP · max 10 MB per fil (50 MB för ZIP) · upp till 10 filer
    </div>
```

- [ ] **Step 2: Branch the upload loop on `.zip`**

In `src/KoalaBooks.Components/Pages/Inbox.razor`, replace the `UploadAsync` method (lines 187-215):

```csharp
    private async Task UploadAsync(IReadOnlyList<IBrowserFile> files)
    {
        if (files is null || files.Count == 0) return;
        _error = null;
        _uploading = true;
        try
        {
            foreach (var file in files)
            {
                const long maxBytes = 10 * 1024 * 1024;
                if (file.Size > maxBytes)
                {
                    _error = $"{file.Name}: för stor (max 10 MB).";
                    continue;
                }
                using var ms = new MemoryStream();
                await file.OpenReadStream(maxBytes).CopyToAsync(ms);
                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
                var (_, err) = await DocumentService.UploadAsync(file.Name, contentType, ms.ToArray());
                if (err is not null)
                    _error = err;
                else
                    Snackbar.Add($"{file.Name} uppladdad.", Severity.Success);
            }
            _page = 1;
            await LoadPageAsync();
        }
        finally { _uploading = false; }
    }
```

with:

```csharp
    private async Task UploadAsync(IReadOnlyList<IBrowserFile> files)
    {
        if (files is null || files.Count == 0) return;
        _error = null;
        _uploading = true;
        try
        {
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

                using var ms = new MemoryStream();
                await file.OpenReadStream(fileMaxBytes).CopyToAsync(ms);

                if (isZip)
                {
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
                var (_, err) = await DocumentService.UploadAsync(file.Name, contentType, ms.ToArray());
                if (err is not null)
                    _error = err;
                else
                    Snackbar.Add($"{file.Name} uppladdad.", Severity.Success);
            }
            _page = 1;
            await LoadPageAsync();
        }
        finally { _uploading = false; }
    }
```

- [ ] **Step 3: Build**

Run: `dotnet build src/KoalaBooks.Web`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Manually verify in the browser**

Run: `dotnet run --project src/KoalaBooks.Web` (or use the `run` skill if this project has one configured)

In the browser, navigate to `/inbox` and:
1. Build a small test zip locally containing e.g. `a.pdf`, `b.png`, and one unsupported file like `notes.txt` (use any real small PDF/PNG on disk, or `zip test.zip a.pdf b.png notes.txt` from a shell).
2. Click "Ladda upp dokument", select the zip, upload it.
3. Confirm: a summary Snackbar appears reporting N imported / 1 skipped (with `notes.txt: Otillåten filtyp.` as the reason), and the imported documents (`a.pdf`, `b.png`) show up as new rows in the inbox table.
4. Upload a single `.pdf` file (not zipped) alongside/after, confirm the existing single-file flow still works unchanged (per-file Snackbar, `.pdf uppladdad.`).

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Inbox.razor
git commit -m "feat: support .zip upload in the document inbox"
```

---

## Self-Review Notes

- **Spec coverage:** container guards (size/entry count/corrupt file) → Task 3; per-entry validation/size → Task 2; happy path/flatten/directory-skip → Task 1; UI wiring + summary reporting → Task 4. All spec sections have a corresponding task.
- **Type consistency:** `ZipImportResult` and `UploadZipAsync`'s tuple return type (`(ZipImportResult? Result, string? Error)`) are defined once in Task 1 and used identically in Tasks 2-4; `entry.Name` (not `entry.FullName`/`Path.GetFileName`) is used consistently across all tasks for both flattening and directory detection.
- **Refinement vs. spec:** the spec sketched `UploadZipAsync` returning a bare `ZipImportResult`; this plan uses `(ZipImportResult? Result, string? Error)` instead, matching the `(Doc, Error)`/`(ContentType, Data, FileName)?` tuple pattern already used by every other fallible method on `DocumentService` (`UploadAsync`, `UploadAndLinkAsync`, `GetDownloadAsync`) — needed so whole-zip rejections (oversized container, too many entries, corrupt file) are distinguishable from a result with an empty `Imported` list.
