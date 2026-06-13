# Inbox Preview, Sorting & Bookkeeping Date — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the blank document preview bug, add a `DocumentDate` bookkeeping field, replace the inbox type dropdown with a read-only badge, add sortable columns, and add a lightweight preview dialog separate from the classify flow.

**Architecture:** One new `PreviewDocumentDialog` Blazor component handles view + metadata editing. The existing `ClassifyDocumentDialog` is untouched. `DocumentService` gains `UpdateMetadataAsync` (replacing `SetTypeAsync`) and server-side sort support. One EF Core migration adds `DocumentDate` to the `Documents` table.

**Tech Stack:** ASP.NET Core 10, Blazor Server (Interactive), EF Core + PostgreSQL, MudBlazor 9, xUnit + Postgres test containers

---

## File Map

| Action | File |
|--------|------|
| Modify | `src/KoalaBooks.Web/Program.cs` |
| Modify | `src/KoalaBooks.Domain/Entities/Document.cs` |
| New    | `src/KoalaBooks.Infrastructure/Migrations/` (generated) |
| Modify | `src/KoalaBooks.Application/Services/DocumentService.cs` |
| Modify | `src/KoalaBooks.Components/Pages/Inbox.razor` |
| New    | `src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor` |
| Modify | `tests/KoalaBooks.Tests/DocumentServiceTests.cs` |
| Modify | `tests/KoalaBooks.Tests/TestFixture.cs` |

---

## Task 1: Fix Content-Disposition — serve documents inline

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs:180`

The endpoint currently calls `Results.File(data, contentType, fileName)`. Passing a filename sets
`Content-Disposition: attachment`, which makes browsers download the file instead of rendering it
in the `<iframe>` or `<img>` in the classify/preview dialog. Drop the filename so the browser renders inline.

- [ ] **Step 1: Apply the fix**

In `src/KoalaBooks.Web/Program.cs`, find the `/documents/{id:int}` endpoint (line ~175) and change:

```csharp
// Before
return result is null
    ? Results.NotFound()
    : Results.File(result.Value.Data, result.Value.ContentType, result.Value.FileName);

// After
return result is null
    ? Results.NotFound()
    : Results.File(result.Value.Data, result.Value.ContentType);
```

- [ ] **Step 2: Commit**

```bash
git add src/KoalaBooks.Web/Program.cs
git commit -m "fix: serve documents inline so iframe preview renders"
```

---

## Task 2: Add DocumentDate to Document entity

**Files:**
- Modify: `src/KoalaBooks.Domain/Entities/Document.cs`

- [ ] **Step 1: Add the property**

Replace the contents of `src/KoalaBooks.Domain/Entities/Document.cs` with:

```csharp
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

    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<SupplierInvoice> SupplierInvoices { get; set; } = [];
    public List<CustomerInvoice> CustomerInvoices { get; set; } = [];
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/KoalaBooks.Domain/KoalaBooks.Domain.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Domain/Entities/Document.cs
git commit -m "feat: add DocumentDate to Document entity"
```

---

## Task 3: Generate EF Core migration

**Files:**
- New: `src/KoalaBooks.Infrastructure/Migrations/` (auto-generated)

- [ ] **Step 1: Add the migration**

```bash
dotnet ef migrations add AddDocumentDate \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: New migration files appear in `src/KoalaBooks.Infrastructure/Migrations/`.

- [ ] **Step 2: Inspect the generated migration**

Open the generated `*_AddDocumentDate.cs`. The `Up` method should contain exactly one `AddColumn`:

```csharp
migrationBuilder.AddColumn<DateOnly>(
    name: "DocumentDate",
    table: "Documents",
    type: "date",
    nullable: true);
```

If it contains anything else (unexpected columns, renames), stop and investigate before continuing.

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Migrations/
git commit -m "feat: migration — add DocumentDate to Documents"
```

---

## Task 4: DocumentService — write failing tests

**Files:**
- Modify: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs`

Add the extractor overload to `TestFixture` and the three new tests. The tests reference `UpdateMetadataAsync` and the `sortBy` parameter — these don't exist yet, so this task also adds the method stubs to make the tests compile (but fail).

- [ ] **Step 1: Add extractor overload to TestFixture**

In `tests/KoalaBooks.Tests/TestFixture.cs`, add after the existing `MakeDocumentService(IDocumentStorage)` overload:

```csharp
public DocumentService MakeDocumentService(IDocumentExtractor extractor) =>
    new DocumentService(Db, new DbDocumentStorage(Db), extractor, _currentUser,
        NullLogger<DocumentService>.Instance);
```

- [ ] **Step 2: Add tests to DocumentServiceTests.cs**

Replace the existing `SetTypeAsync_UpdatesClassifiedType` test and add three new tests. The full replacement block (find the existing test and replace with):

```csharp
[Fact]
public async Task UpdateMetadataAsync_SetsTypeAndDate()
{
    var svc = _fx.MakeDocumentService();
    var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", []);
    var date = new DateOnly(2026, 3, 15);

    var err = await svc.UpdateMetadataAsync(doc!.Id, "CustomerInvoice", date);

    Assert.Null(err);
    var pending = await svc.GetPendingAsync();
    var updated = pending.First(d => d.Id == doc.Id);
    Assert.Equal("CustomerInvoice", updated.ClassifiedType);
    Assert.Equal(date, updated.DocumentDate);
}

[Fact]
public async Task UploadAsync_PopulatesDocumentDateFromExtractor()
{
    var expectedDate = new DateOnly(2026, 3, 15);
    var extractor = new StubExtractor(new ExtractionResult(
        "SupplierInvoice", "ACME AB", 1000m, 250m, expectedDate, null, "INV-001"));
    var svc = _fx.MakeDocumentService(extractor);

    var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", [1]);

    Assert.Equal(expectedDate, doc!.DocumentDate);
}

[Fact]
public async Task GetPendingAsync_SortsByDocumentDate()
{
    var svc = _fx.MakeDocumentService();
    var (d1, _) = await svc.UploadAsync("a.pdf", "application/pdf", [1]);
    var (d2, _) = await svc.UploadAsync("b.pdf", "application/pdf", [2]);

    await svc.UpdateMetadataAsync(d1!.Id, null, new DateOnly(2026, 1, 1));
    await svc.UpdateMetadataAsync(d2!.Id, null, new DateOnly(2026, 6, 1));

    var ascResult = await svc.GetPendingAsync(sortBy: "documentDate", sortAsc: true);
    Assert.Equal(d1.Id, ascResult[0].Id);
    Assert.Equal(d2.Id, ascResult[1].Id);

    var descResult = await svc.GetPendingAsync(sortBy: "documentDate", sortAsc: false);
    Assert.Equal(d2.Id, descResult[0].Id);
    Assert.Equal(d1.Id, descResult[1].Id);
}
```

- [ ] **Step 3: Add StubExtractor at bottom of DocumentServiceTests.cs**

After the existing `file class FailingStorage`, add:

```csharp
file class StubExtractor(ExtractionResult result) : IDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data) =>
        Task.FromResult(result);
}
```

- [ ] **Step 4: Add method stubs to DocumentService**

In `src/KoalaBooks.Application/Services/DocumentService.cs`, add these stubs so the tests compile. Do NOT implement them yet.

After `SetTypeAsync`, add:

```csharp
public Task<string?> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate)
    => throw new NotImplementedException();
```

Also add `sortBy` and `sortAsc` parameters to `GetPendingAsync` with default values that preserve existing behavior:

```csharp
public Task<List<DocumentMeta>> GetPendingAsync(
    string? typeFilter = null,
    int skip = 0,
    int? take = null,
    string sortBy = "uploadedAt",
    bool sortAsc = false)
```

Leave the body unchanged for now (it will still only sort by `UploadedAt`).

Also add `DocumentDate` to the `DocumentMeta` class:

```csharp
public DateOnly? DocumentDate { get; set; }
```

- [ ] **Step 5: Run tests to verify they fail**

```bash
dotnet test tests/KoalaBooks.Tests --filter "UpdateMetadataAsync_SetsTypeAndDate|UploadAsync_PopulatesDocumentDateFromExtractor|GetPendingAsync_SortsByDocumentDate" -v
```

Expected: Three test failures (NotImplementedException for UpdateMetadataAsync, missing DocumentDate for the extractor test, wrong sort order for the sort test).

- [ ] **Step 6: Commit**

```bash
git add tests/KoalaBooks.Tests/DocumentServiceTests.cs \
        tests/KoalaBooks.Tests/TestFixture.cs \
        src/KoalaBooks.Application/Services/DocumentService.cs
git commit -m "test: failing tests for UpdateMetadataAsync, DocumentDate population, sort"
```

---

## Task 5: DocumentService — implement to make tests pass

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DocumentService.cs`

- [ ] **Step 1: Replace UpdateMetadataAsync stub with implementation**

Find and replace the stub:

```csharp
public async Task<string?> UpdateMetadataAsync(int documentId, string? classifiedType, DateOnly? documentDate)
{
    var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
    if (doc is null) return "Dokumentet hittades inte.";
    doc.ClassifiedType = classifiedType;
    doc.DocumentDate = documentDate;
    await db.SaveChangesAsync();
    return null;
}
```

- [ ] **Step 2: Remove SetTypeAsync**

Delete the `SetTypeAsync` method entirely. Verify no remaining callers with:

```bash
grep -rn "SetTypeAsync" src/ tests/
```

Expected: zero hits.

- [ ] **Step 3: Populate DocumentDate in UploadAsync**

In `UploadAsync`, inside the extractor try block, add one line after `doc.ExtractedDataJson = ...`:

```csharp
try
{
    var result = await extractor.ExtractAsync(fileName, contentType, data);
    doc.SuggestedType = result.SuggestedType;
    doc.ExtractedDataJson = result.SuggestedType is not null
        ? JsonSerializer.Serialize(result)
        : null;
    doc.DocumentDate = result.InvoiceDate;  // ← add this line
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Extraction failed for {FileName} — upload proceeds without suggestion", fileName);
}
```

- [ ] **Step 4: Implement sorting in GetPendingAsync**

Replace the body of `GetPendingAsync` with:

```csharp
public Task<List<DocumentMeta>> GetPendingAsync(
    string? typeFilter = null,
    int skip = 0,
    int? take = null,
    string sortBy = "uploadedAt",
    bool sortAsc = false)
{
    var base2 = PendingQuery(typeFilter);
    IQueryable<Document> ordered = (sortBy, sortAsc) switch
    {
        ("fileName",     true)  => base2.OrderBy(d => d.FileName),
        ("fileName",     false) => base2.OrderByDescending(d => d.FileName),
        ("documentDate", true)  => base2.OrderBy(d => d.DocumentDate),
        ("documentDate", false) => base2.OrderByDescending(d => d.DocumentDate),
        (_,              true)  => base2.OrderBy(d => d.UploadedAt),
        _                       => base2.OrderByDescending(d => d.UploadedAt),
    };
    var q = ordered.Skip(skip);
    if (take.HasValue) q = q.Take(take.Value);
    return SelectMetaAsync(q);
}
```

- [ ] **Step 5: Add DocumentDate to SelectMetaAsync projection**

Find `SelectMetaAsync` and add `DocumentDate = d.DocumentDate` to the projection:

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
        DocumentDate = d.DocumentDate
    }).ToListAsync();
```

- [ ] **Step 6: Run the three new tests**

```bash
dotnet test tests/KoalaBooks.Tests --filter "UpdateMetadataAsync_SetsTypeAndDate|UploadAsync_PopulatesDocumentDateFromExtractor|GetPendingAsync_SortsByDocumentDate" -v
```

Expected: All three PASS.

- [ ] **Step 7: Run the full test suite**

```bash
dotnet test tests/KoalaBooks.Tests -v
```

Expected: All tests pass. If `SetTypeAsync_UpdatesClassifiedType` shows as a missing method error, you forgot to remove it from tests — delete that test method.

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs
git commit -m "feat: UpdateMetadataAsync, DocumentDate population, server-side sort"
```

---

## Task 6: Inbox table UI

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Inbox.razor`

- [ ] **Step 1: Update the table header — add sort and new columns**

Replace the `<thead>` block:

```html
<thead>
    <tr>
        <th @onclick='() => SortByAsync("fileName")' style="cursor:pointer; user-select:none;">
            Filnamn@(SortIndicator("fileName"))
        </th>
        <th style="width:90px; text-align:right;">Storlek</th>
        <th style="width:150px; cursor:pointer; user-select:none;"
            @onclick='() => SortByAsync("uploadedAt")'>
            Uppladdad@(SortIndicator("uploadedAt"))
        </th>
        <th style="width:150px; cursor:pointer; user-select:none;"
            @onclick='() => SortByAsync("documentDate")'>
            Bokföringsdatum@(SortIndicator("documentDate"))
        </th>
        <th style="width:140px;">Typ</th>
        <th style="width:150px;">Åtgärder</th>
    </tr>
</thead>
```

- [ ] **Step 2: Update the table rows**

Replace the `<tr>` inside `@foreach`:

```html
<tr>
    <td style="font-weight:500;">@doc.FileName</td>
    <td style="text-align:right; color:#64748b; font-size:0.875rem;">@doc.FileSizeDisplay</td>
    <td style="color:#64748b; font-size:0.875rem;">@doc.UploadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")</td>
    <td style="color:#64748b; font-size:0.875rem;">@(doc.DocumentDate?.ToString("yyyy-MM-dd") ?? "—")</td>
    <td>
        @if (doc.ClassifiedType is not null)
        {
            <span style="font-size:0.75rem; padding:2px 8px; border-radius:9999px; background:#e2e8f0; color:#475569;">
                @TypeLabel(doc.ClassifiedType)
            </span>
        }
    </td>
    <td style="display:flex; gap:0.3rem; flex-wrap:wrap;">
        <button class="btn btn-sm btn-secondary" @onclick="() => OpenPreviewDialog(doc)" title="Förhandsgranska">👁</button>
        <button class="btn btn-sm btn-primary" @onclick="() => OpenClassifyDialog(doc)">Bokför</button>
        <button class="btn btn-sm btn-danger" @onclick="() => DeleteDocAsync(doc.Id)">🗑</button>
    </td>
</tr>
```

- [ ] **Step 3: Add PreviewDocumentDialog rendering**

After the existing `@if (_classifyDoc is not null)` block, add:

```html
@if (_previewDoc is not null)
{
    <PreviewDocumentDialog Doc="_previewDoc"
                           DocumentProvider="DocumentProvider"
                           OnSaved="OnDocumentSaved"
                           OnClose="() => _previewDoc = null"
                           OnClassify="OpenClassifyFromPreview" />
}
```

- [ ] **Step 4: Update @code — state, sort helpers, new handlers**

In the `@code` block:

**Add fields** (after existing private fields):
```csharp
private string _sortBy = "uploadedAt";
private bool _sortAsc = false;
private DocumentMeta? _previewDoc;
```

**Remove** the `_classifyDoc` field (it already exists — leave it). Remove the `SetTypeAsync` call and the `SetTypeAsync` method entirely.

**Update LoadPageAsync** to pass sort params:
```csharp
private async Task LoadPageAsync()
{
    _isLoading = true;
    var skip = (_page - 1) * PageSize;
    _docs = await DocumentService.GetPendingAsync(_filter, skip, PageSize, _sortBy, _sortAsc);
    _totalCount = await DocumentService.GetPendingCountAsync(_filter);
    _isLoading = false;
}
```

**Add sort helpers**:
```csharp
private async Task SortByAsync(string column)
{
    if (_sortBy == column) _sortAsc = !_sortAsc;
    else { _sortBy = column; _sortAsc = false; }
    _page = 1;
    await LoadPageAsync();
}

private string SortIndicator(string column) =>
    _sortBy != column ? "" : _sortAsc ? " ▲" : " ▼";

private static string TypeLabel(string? type) => type switch
{
    "SupplierInvoice" => "Leverantörsfaktura",
    "CustomerInvoice" => "Kundfaktura",
    "JournalEntry"    => "Verifikation",
    _                 => ""
};
```

**Add preview handlers**:
```csharp
private void OpenPreviewDialog(DocumentMeta doc) => _previewDoc = doc;

private async Task OnDocumentSaved()
{
    _previewDoc = null;
    await LoadPageAsync();
    Snackbar.Add("Sparat.", Severity.Success);
}

private void OpenClassifyFromPreview()
{
    var doc = _previewDoc;
    _previewDoc = null;
    _classifyDoc = doc;
}
```

**Remove** the `SetTypeAsync` method:
```csharp
// DELETE this entire method:
private async Task SetTypeAsync(int docId, string? type) { ... }
```

- [ ] **Step 5: Verify build**

```bash
dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj
```

Expected: 0 errors. If there are errors about missing `PreviewDocumentDialog`, that's expected — it's created in Task 7.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Inbox.razor
git commit -m "feat: inbox — sort headers, bookkeeping date column, type badge, preview button"
```

---

## Task 7: PreviewDocumentDialog component

**Files:**
- Create: `src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor`

- [ ] **Step 1: Create the component**

Create `src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor` with:

```razor
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Interfaces
@using MudBlazor
@using System.Text.Json

<MudDialog Style="max-width:900px; width:95vw;">
    <TitleContent>
        <MudText Typo="Typo.h6">Förhandsgranskning</MudText>
    </TitleContent>
    <DialogContent>
        <div style="display:flex; gap:0; height:520px;">

            <!-- Left: document preview -->
            <div style="flex:1; border-right:1px solid #e2e8f0; display:flex; flex-direction:column; overflow:hidden;">
                @if (Doc.ContentType == "application/pdf")
                {
                    <iframe src="@DocumentProvider.GetDownloadUrl(Doc.Id)"
                            style="flex:1; border:none;" title="@Doc.FileName"></iframe>
                }
                else
                {
                    <div style="flex:1; display:flex; align-items:center; justify-content:center; overflow:hidden;">
                        <img src="@DocumentProvider.GetDownloadUrl(Doc.Id)"
                             style="max-width:100%; max-height:100%; object-fit:contain;" alt="@Doc.FileName" />
                    </div>
                }
                <div style="padding:6px 10px; font-size:0.75rem; color:#64748b; border-top:1px solid #f1f5f9;">
                    @Doc.FileName &nbsp;·&nbsp; @Doc.FileSizeDisplay
                </div>
            </div>

            <!-- Right: metadata form -->
            <div style="flex:1; padding:1.25rem; overflow-y:auto; display:flex; flex-direction:column; gap:0.75rem;">

                @if (_error is not null)
                {
                    <MudAlert Severity="Severity.Error" Dense="true">@_error</MudAlert>
                }

                <div class="form-group">
                    <label style="font-weight:600;">Typ</label>
                    <select @bind="_type" style="width:100%;">
                        <option value="">— Välj typ —</option>
                        <option value="SupplierInvoice">Leverantörsfaktura</option>
                        <option value="CustomerInvoice">Kundfaktura</option>
                        <option value="JournalEntry">Verifikation</option>
                    </select>
                </div>

                <div class="form-group">
                    <label style="font-weight:600;">Bokföringsdatum</label>
                    <DateInput @bind-Value="_documentDate" />
                </div>

                @if (_extractedSummary is not null)
                {
                    <div style="background:#f8fafc; border:1px solid #e2e8f0; border-radius:6px; padding:0.75rem; font-size:0.8rem; color:#475569;">
                        <div style="font-weight:600; margin-bottom:0.4rem; color:#334155;">Extraherat</div>
                        @if (_extractedSummary.Supplier is not null)
                        {
                            <div>Leverantör: @_extractedSummary.Supplier</div>
                        }
                        @if (_extractedSummary.Amount.HasValue)
                        {
                            <div>Belopp: @_extractedSummary.Amount.Value.ToString("N2") kr</div>
                        }
                        @if (_extractedSummary.InvoiceNumber is not null)
                        {
                            <div>Fakturanummer: @_extractedSummary.InvoiceNumber</div>
                        }
                    </div>
                }

                <div style="margin-top:auto; display:flex; gap:0.5rem; padding-top:0.75rem; border-top:1px solid #f1f5f9;">
                    <button class="btn btn-primary" @onclick="SaveAsync" disabled="@_saving">
                        @(_saving ? "Sparar..." : "Spara")
                    </button>
                    <button class="btn btn-secondary" @onclick="OpenClassifyAsync">Bokför</button>
                    <button class="btn btn-secondary" @onclick="OnClose">Avbryt</button>
                </div>
            </div>
        </div>
    </DialogContent>
</MudDialog>

@code {
    [Parameter, EditorRequired] public DocumentMeta Doc { get; set; } = default!;
    [Parameter, EditorRequired] public IDocumentProvider DocumentProvider { get; set; } = default!;
    [Parameter, EditorRequired] public EventCallback OnSaved { get; set; }
    [Parameter, EditorRequired] public EventCallback OnClose { get; set; }
    [Parameter, EditorRequired] public EventCallback OnClassify { get; set; }

    [Inject] private DocumentService DocumentService { get; set; } = default!;

    private string _type = "";
    private DateTime _documentDate = DateTime.Today;
    private bool _saving;
    private string? _error;
    private ExtractionResult? _extractedSummary;

    protected override void OnInitialized()
    {
        _type = Doc.ClassifiedType ?? "";

        ExtractionResult? extracted = null;
        if (Doc.ExtractedDataJson is not null)
        {
            try { extracted = JsonSerializer.Deserialize<ExtractionResult>(Doc.ExtractedDataJson); }
            catch { }
        }

        // Pre-fill date: prefer persisted DocumentDate, fall back to extracted InvoiceDate, then today
        if (Doc.DocumentDate.HasValue)
            _documentDate = Doc.DocumentDate.Value.ToDateTime(TimeOnly.MinValue);
        else if (extracted?.InvoiceDate.HasValue == true)
            _documentDate = extracted.InvoiceDate!.Value.ToDateTime(TimeOnly.MinValue);

        if (extracted is not null &&
            (extracted.Supplier is not null || extracted.Amount.HasValue || extracted.InvoiceNumber is not null))
            _extractedSummary = extracted;
    }

    private async Task SaveAsync()
    {
        _error = null;
        _saving = true;
        try
        {
            var type = string.IsNullOrEmpty(_type) ? null : _type;
            var date = DateOnly.FromDateTime(_documentDate);
            var err = await DocumentService.UpdateMetadataAsync(Doc.Id, type, date);
            if (err is not null) { _error = err; return; }
            await OnSaved.InvokeAsync();
        }
        finally { _saving = false; }
    }

    private async Task OpenClassifyAsync() => await OnClassify.InvokeAsync();
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj
```

Expected: 0 errors.

- [ ] **Step 3: Run the full test suite**

```bash
dotnet test tests/KoalaBooks.Tests -v
```

Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Shared/PreviewDocumentDialog.razor
git commit -m "feat: PreviewDocumentDialog — view document and set type/bookkeeping date"
```

---

## Task 8: Create PR

- [ ] **Step 1: Push and open PR**

```bash
git push origin main
gh pr create \
  --title "feat: inbox preview, bookkeeping date, sorting" \
  --body "$(cat <<'EOF'
## Summary

- Fixes blank iframe preview (Content-Disposition: attachment → inline)
- Adds \`DocumentDate\` (bokföringsdatum) to \`Document\` entity, auto-populated from PDF extraction
- Replaces editable type dropdown in inbox table with read-only badge
- Adds sortable columns (Filnamn, Uppladdad, Bokföringsdatum)
- Adds 👁 preview button opening \`PreviewDocumentDialog\`: view document + set type + set bookkeeping date
- Extracted summary (supplier, amount, invoice number) shown read-only in preview dialog
- \"Bokför\" button in preview dialog transitions to the full classify flow

## Test plan

- [ ] Upload a PDF — confirm DocumentDate is populated from extracted InvoiceDate if present
- [ ] Click 👁 on a PDF — confirm preview loads the document (not blank)
- [ ] Click 👁 on an image — confirm image renders
- [ ] Edit type and date in preview dialog, click Spara — confirm inbox row updates
- [ ] Click Klassificera inside preview dialog — confirm classify dialog opens
- [ ] Click sortable column headers — confirm order changes, ▲/▼ indicator appears
- [ ] All existing tests pass

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```
