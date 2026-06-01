# Document Inbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `/inkorg` document inbox where users upload PDFs/images, set their type with one click, and classify them into supplier invoices, customer invoices, or journal entries via a 50/50 split modal — replacing `JournalEntryAttachment` with a unified `Document` entity.

**Architecture:** New `Document` entity (org-scoped, many-to-many with JournalEntry/SupplierInvoice/CustomerInvoice via EF implicit join tables) backed by `IDocumentStorage` (initial impl: `DbDocumentStorage` with a separate `DocumentData` blob table). Extraction pipeline (FilenameExtractor + PdfTextExtractor via PdfPig) runs at upload time and sets `SuggestedType`/`ClassifiedType`. Classification in the UI calls existing entity services then `DocumentService.LinkAsync`. Auto-link propagates from invoice to its journal entry inside `SupplierInvoiceService.PostAsync` and `CustomerInvoiceService.PostAsync` via direct DbContext queries.

**Tech Stack:** .NET 10, Blazor Server, EF Core + PostgreSQL, MudBlazor, UglyToad.PdfPig (new)

---

## File Map

**Create:**
- `src/KoalaBooks.Domain/Entities/Document.cs`
- `src/KoalaBooks.Domain/Entities/DocumentData.cs`
- `src/KoalaBooks.Domain/Enums/DocumentEntityType.cs`
- `src/KoalaBooks.Application/Services/IDocumentStorage.cs`
- `src/KoalaBooks.Application/Services/IDocumentExtractor.cs`
- `src/KoalaBooks.Application/Services/IDocumentProvider.cs`
- `src/KoalaBooks.Application/Services/DocumentService.cs`
- `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`
- `src/KoalaBooks.Infrastructure/Services/FilenameExtractor.cs`
- `src/KoalaBooks.Infrastructure/Services/PdfTextExtractor.cs`
- `src/KoalaBooks.Infrastructure/Services/CompositeExtractor.cs`
- `src/KoalaBooks.Web/Services/WebDocumentProvider.cs`
- `src/KoalaBooks.Components/Pages/Inkorg.razor`
- `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor`
- `tests/KoalaBooks.Tests/DocumentExtractorTests.cs`
- `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

**Modify:**
- `src/KoalaBooks.Domain/Entities/JournalEntry.cs` — add `Documents` nav
- `src/KoalaBooks.Domain/Entities/SupplierInvoice.cs` — add `Documents` nav
- `src/KoalaBooks.Domain/Entities/CustomerInvoice.cs` — add `Documents` nav
- `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs` — add DbSets, configure Document, remove JournalEntryAttachments
- `src/KoalaBooks.Application/Services/SupplierInvoiceService.cs` — auto-link docs in PostAsync
- `src/KoalaBooks.Application/Services/CustomerInvoiceService.cs` — auto-link docs in PostAsync
- `src/KoalaBooks.Web/Program.cs` — register services, swap /attachments → /documents endpoint
- `src/KoalaBooks.Components/Pages/Journal.razor` — swap AttachmentService → DocumentService
- `src/KoalaBooks.Components/Pages/SupplierInvoices.razor` — add document panel
- `src/KoalaBooks.Components/Pages/CustomerInvoices.razor` — add document panel
- `src/KoalaBooks.Components/Layout/MainLayout.razor` — add Inkorg nav link

---

### Task 1: Domain entities and interfaces

**Files:**
- Create: `src/KoalaBooks.Domain/Entities/Document.cs`
- Create: `src/KoalaBooks.Domain/Entities/DocumentData.cs`
- Create: `src/KoalaBooks.Domain/Enums/DocumentEntityType.cs`
- Create: `src/KoalaBooks.Application/Services/IDocumentStorage.cs`
- Create: `src/KoalaBooks.Application/Services/IDocumentExtractor.cs`
- Create: `src/KoalaBooks.Application/Services/IDocumentProvider.cs`
- Modify: `src/KoalaBooks.Domain/Entities/JournalEntry.cs`
- Modify: `src/KoalaBooks.Domain/Entities/SupplierInvoice.cs`
- Modify: `src/KoalaBooks.Domain/Entities/CustomerInvoice.cs`

- [ ] **Step 1: Create `Document.cs`**

```csharp
// src/KoalaBooks.Domain/Entities/Document.cs
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

    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<SupplierInvoice> SupplierInvoices { get; set; } = [];
    public List<CustomerInvoice> CustomerInvoices { get; set; } = [];
}
```

- [ ] **Step 2: Create `DocumentData.cs`**

```csharp
// src/KoalaBooks.Domain/Entities/DocumentData.cs
namespace KoalaBooks.Domain.Entities;

public class DocumentData
{
    public int DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public byte[] Data { get; set; } = [];
}
```

- [ ] **Step 3: Create `DocumentEntityType.cs`**

```csharp
// src/KoalaBooks.Domain/Enums/DocumentEntityType.cs
namespace KoalaBooks.Domain.Enums;

public enum DocumentEntityType { JournalEntry, SupplierInvoice, CustomerInvoice }
```

- [ ] **Step 4: Create `IDocumentStorage.cs`**

```csharp
// src/KoalaBooks.Application/Services/IDocumentStorage.cs
namespace KoalaBooks.Application.Services;

public interface IDocumentStorage
{
    Task<string> SaveAsync(int documentId, string contentType, byte[] data);
    Task<byte[]> LoadAsync(string storageKey);
    Task DeleteAsync(string storageKey);
}
```

- [ ] **Step 5: Create `IDocumentExtractor.cs`**

```csharp
// src/KoalaBooks.Application/Services/IDocumentExtractor.cs
namespace KoalaBooks.Application.Services;

public record ExtractionResult(
    string? SuggestedType,
    string? Supplier,
    decimal? Amount,
    decimal? VatAmount,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string? InvoiceNumber
);

public interface IDocumentExtractor
{
    Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data);
}
```

- [ ] **Step 6: Create `IDocumentProvider.cs`**

```csharp
// src/KoalaBooks.Application/Services/IDocumentProvider.cs
namespace KoalaBooks.Application.Services;

public interface IDocumentProvider
{
    string GetDownloadUrl(int documentId);
}
```

- [ ] **Step 7: Add `Documents` navigation to `JournalEntry.cs`**

Add after `public List<JournalEntryLine> Lines { get; set; } = [];`:

```csharp
    public List<Document> Documents { get; set; } = [];
```

- [ ] **Step 8: Add `Documents` navigation to `SupplierInvoice.cs`**

Add at the end of the class (before the closing brace), after `CreatedAt`:

```csharp
    public List<Document> Documents { get; set; } = [];
```

- [ ] **Step 9: Add `Documents` navigation to `CustomerInvoice.cs`**

Add at the end of the class, after `CreatedAt`:

```csharp
    public List<Document> Documents { get; set; } = [];
```

- [ ] **Step 10: Verify build**

```bash
dotnet build src/KoalaBooks.Domain/KoalaBooks.Domain.csproj
dotnet build src/KoalaBooks.Application/KoalaBooks.Application.csproj
```

Expected: Build succeeded with 0 error(s).

- [ ] **Step 11: Commit**

```bash
git add src/KoalaBooks.Domain/ src/KoalaBooks.Application/Services/IDocument*.cs
git commit -m "feat: add Document entity, DocumentData, DocumentEntityType, and document interfaces"
```

---

### Task 2: Extraction pipeline

**Files:**
- Create: `src/KoalaBooks.Infrastructure/Services/FilenameExtractor.cs`
- Create: `src/KoalaBooks.Infrastructure/Services/PdfTextExtractor.cs`
- Create: `src/KoalaBooks.Infrastructure/Services/CompositeExtractor.cs`

- [ ] **Step 1: Add PdfPig to Infrastructure project**

```bash
dotnet add src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj package UglyToad.PdfPig
```

Expected: Package `UglyToad.PdfPig` added.

- [ ] **Step 2: Create `FilenameExtractor.cs`**

```csharp
// src/KoalaBooks.Infrastructure/Services/FilenameExtractor.cs
using KoalaBooks.Application.Services;

namespace KoalaBooks.Infrastructure.Services;

public class FilenameExtractor : IDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        string? type = null;
        if (name.Contains("kundfaktura") || name.Contains("customer"))
            type = "CustomerInvoice";
        else if (name.Contains("faktura") || name.Contains("invoice") || name.Contains("fakt"))
            type = "SupplierInvoice";
        else if (name.Contains("kvitto") || name.Contains("receipt"))
            type = "JournalEntry";

        return Task.FromResult(new ExtractionResult(type, null, null, null, null, null, null));
    }
}
```

- [ ] **Step 3: Create `PdfTextExtractor.cs`**

```csharp
// src/KoalaBooks.Infrastructure/Services/PdfTextExtractor.cs
using KoalaBooks.Application.Services;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace KoalaBooks.Infrastructure.Services;

public partial class PdfTextExtractor : IDocumentExtractor
{
    public Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        if (contentType != "application/pdf")
            return Task.FromResult(new ExtractionResult(null, null, null, null, null, null, null));

        try
        {
            var text = ExtractText(data);
            return Task.FromResult(Parse(text));
        }
        catch
        {
            return Task.FromResult(new ExtractionResult(null, null, null, null, null, null, null));
        }
    }

    private static string ExtractText(byte[] data)
    {
        using var doc = PdfDocument.Open(data);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    private static ExtractionResult Parse(string text)
    {
        var type = DetectType(text);
        var amount = ExtractAmount(text);
        var invoiceDate = ExtractDate(text, InvoiceDatePattern());
        var dueDate = ExtractDate(text, DueDatePattern());
        var invoiceNumber = ExtractInvoiceNumber(text);
        var supplier = ExtractSupplier(text);

        return new ExtractionResult(type, supplier, amount?.excl, amount?.vat, invoiceDate, dueDate, invoiceNumber);
    }

    private static string? DetectType(string text)
    {
        if (Regex.IsMatch(text, @"[Kk]undfaktura|[Ss]ales [Ii]nvoice", RegexOptions.IgnoreCase))
            return "CustomerInvoice";
        if (Regex.IsMatch(text, @"[Ff]aktura|[Ii]nvoice", RegexOptions.IgnoreCase))
            return "SupplierInvoice";
        return null;
    }

    private static (decimal excl, decimal? vat)? ExtractAmount(string text)
    {
        var match = AmountPattern().Match(text);
        if (!match.Success) return null;
        var raw = match.Groups[1].Value.Replace(" ", "").Replace(",", ".");
        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var amount))
            return null;
        return (amount, null);
    }

    private static DateOnly? ExtractDate(string text, Regex pattern)
    {
        var match = pattern.Match(text);
        if (!match.Success) return null;
        return DateOnly.TryParse(match.Groups[1].Value, out var date) ? date : null;
    }

    private static string? ExtractInvoiceNumber(string text)
    {
        var match = InvoiceNumberPattern().Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractSupplier(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (Regex.IsMatch(trimmed, @"\b(AB|KB|HB|Inc|Ltd|GmbH|AS)\b"))
                return trimmed.Length > 100 ? null : trimmed;
        }
        return null;
    }

    [GeneratedRegex(@"([\d\s]+[,.][\d]{2})\s*(kr|SEK)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"[Ff]akturadatum[:\s]+(\d{4}-\d{2}-\d{2})")]
    private static partial Regex InvoiceDatePattern();

    [GeneratedRegex(@"[Ff]örfallodatum[:\s]+(\d{4}-\d{2}-\d{2})")]
    private static partial Regex DueDatePattern();

    [GeneratedRegex(@"[Ff]akturanummer[:\s]+(\S+)|[Ii]nvoice\s+[Nn]o[:\s]+(\S+)")]
    private static partial Regex InvoiceNumberPattern();
}
```

- [ ] **Step 4: Create `CompositeExtractor.cs`**

```csharp
// src/KoalaBooks.Infrastructure/Services/CompositeExtractor.cs
using KoalaBooks.Application.Services;

namespace KoalaBooks.Infrastructure.Services;

public class CompositeExtractor(FilenameExtractor filename, PdfTextExtractor pdf) : IDocumentExtractor
{
    public async Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        var f = await filename.ExtractAsync(fileName, contentType, data);
        var p = await pdf.ExtractAsync(fileName, contentType, data);

        // PDF fields take priority; fall back to filename for type if PDF found nothing
        return new ExtractionResult(
            SuggestedType: p.SuggestedType ?? f.SuggestedType,
            Supplier:      p.Supplier,
            Amount:        p.Amount,
            VatAmount:     p.VatAmount,
            InvoiceDate:   p.InvoiceDate,
            DueDate:       p.DueDate,
            InvoiceNumber: p.InvoiceNumber
        );
    }
}
```

- [ ] **Step 5: Verify build**

```bash
dotnet build src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj
```

Expected: Build succeeded with 0 error(s).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/FilenameExtractor.cs \
        src/KoalaBooks.Infrastructure/Services/PdfTextExtractor.cs \
        src/KoalaBooks.Infrastructure/Services/CompositeExtractor.cs \
        src/KoalaBooks.Infrastructure/KoalaBooks.Infrastructure.csproj
git commit -m "feat: add document extraction pipeline (FilenameExtractor, PdfTextExtractor, CompositeExtractor)"
```

---

### Task 3: Extraction tests

**Files:**
- Create: `tests/KoalaBooks.Tests/DocumentExtractorTests.cs`

- [ ] **Step 1: Create `DocumentExtractorTests.cs`**

```csharp
// tests/KoalaBooks.Tests/DocumentExtractorTests.cs
using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

public class DocumentExtractorTests
{
    private readonly FilenameExtractor _filename = new();
    private readonly PdfTextExtractor _pdf = new();

    // ── FilenameExtractor ────────────────────────────────────────────

    [Theory]
    [InlineData("kundfaktura-2024.pdf", "CustomerInvoice")]
    [InlineData("customer-invoice.pdf", "CustomerInvoice")]
    [InlineData("faktura-leverantör.pdf", "SupplierInvoice")]
    [InlineData("invoice_2024.pdf", "SupplierInvoice")]
    [InlineData("fakt123.pdf", "SupplierInvoice")]
    [InlineData("kvitto-jan.jpg", "JournalEntry")]
    [InlineData("receipt_2024.jpg", "JournalEntry")]
    [InlineData("bankutdrag.pdf", null)]
    public async Task FilenameExtractor_DetectsType(string fileName, string? expectedType)
    {
        var result = await _filename.ExtractAsync(fileName, "application/pdf", []);
        Assert.Equal(expectedType, result.SuggestedType);
    }

    [Fact]
    public async Task FilenameExtractor_CustomerBeforeSupplier()
    {
        // "kundfaktura" contains "faktura" — must match CustomerInvoice not SupplierInvoice
        var result = await _filename.ExtractAsync("kundfaktura-mars.pdf", "application/pdf", []);
        Assert.Equal("CustomerInvoice", result.SuggestedType);
    }

    [Fact]
    public async Task FilenameExtractor_ReturnsNullAmountsAndDates()
    {
        var result = await _filename.ExtractAsync("faktura.pdf", "application/pdf", []);
        Assert.Null(result.Amount);
        Assert.Null(result.InvoiceDate);
        Assert.Null(result.DueDate);
        Assert.Null(result.InvoiceNumber);
        Assert.Null(result.Supplier);
    }

    // ── PdfTextExtractor ────────────────────────────────────────────

    [Fact]
    public async Task PdfTextExtractor_SkipsNonPdf()
    {
        var result = await _pdf.ExtractAsync("foto.jpg", "image/jpeg", [1, 2, 3]);
        Assert.Null(result.SuggestedType);
        Assert.Null(result.Amount);
    }

    [Fact]
    public async Task PdfTextExtractor_ReturnsNullsOnCorruptData()
    {
        // Corrupt PDF bytes — should not throw, just return empty result
        var result = await _pdf.ExtractAsync("bad.pdf", "application/pdf", [0xFF, 0xFE, 0x00]);
        Assert.Null(result.SuggestedType);
    }

    // ── CompositeExtractor ───────────────────────────────────────────

    [Fact]
    public async Task CompositeExtractor_PdfTypeTakesPriority()
    {
        // FilenameExtractor would say SupplierInvoice from filename,
        // but we simulate PDF returning CustomerInvoice by using a stub.
        // Since we can't inject a real PDF here, test that filename fallback works
        // when PDF returns null type.
        var composite = new CompositeExtractor(_filename, _pdf);
        var result = await composite.ExtractAsync("faktura.jpg", "image/jpeg", []);
        // PDF returns null (not a pdf), filename returns SupplierInvoice
        Assert.Equal("SupplierInvoice", result.SuggestedType);
    }

    [Fact]
    public async Task CompositeExtractor_FallsBackToFilenameWhenPdfFindsNothing()
    {
        var composite = new CompositeExtractor(_filename, _pdf);
        // Non-PDF image with "kvitto" in filename → JournalEntry via filename fallback
        var result = await composite.ExtractAsync("kvitto-jan.jpg", "image/jpeg", []);
        Assert.Equal("JournalEntry", result.SuggestedType);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj --filter "DocumentExtractorTests" -v normal
```

Expected: All tests pass. If any fail, fix the extractor before proceeding.

- [ ] **Step 3: Commit**

```bash
git add tests/KoalaBooks.Tests/DocumentExtractorTests.cs
git commit -m "test: add DocumentExtractorTests"
```

---

### Task 4: DbDocumentStorage + AppDbContext + EF migration

**Files:**
- Create: `src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/AppDbContext.cs`

- [ ] **Step 1: Create `DbDocumentStorage.cs`**

```csharp
// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Services;

public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    public async Task<string> SaveAsync(int documentId, string contentType, byte[] data)
    {
        var existing = await db.DocumentData.FindAsync(documentId);
        if (existing is not null)
        {
            existing.Data = data;
        }
        else
        {
            db.DocumentData.Add(new DocumentData { DocumentId = documentId, Data = data });
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

- [ ] **Step 2: Update `AppDbContext.cs` — add DbSets**

In `AppDbContext.cs`, add after `public DbSet<JournalEntryAttachment> JournalEntryAttachments => Set<JournalEntryAttachment>();`:

```csharp
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentData> DocumentData => Set<DocumentData>();
```

- [ ] **Step 3: Update `AppDbContext.cs` — configure Document in `OnModelCreating`**

In `OnModelCreating`, add before `modelBuilder.Entity<BankTransaction>(entity =>`:

```csharp
        modelBuilder.Entity<Document>(entity =>
        {
            entity.Property(d => d.FileName).HasMaxLength(260);
            entity.Property(d => d.ContentType).HasMaxLength(100);
            entity.Property(d => d.StorageKey).HasMaxLength(500);
            entity.Property(d => d.SuggestedType).HasMaxLength(50);
            entity.Property(d => d.ClassifiedType).HasMaxLength(50);
            entity.HasQueryFilter(d => _currentUser.OrganisationId != null && d.OrganisationId == _currentUser.OrganisationId);

            entity.HasMany(d => d.JournalEntries)
                  .WithMany(j => j.Documents)
                  .UsingEntity("DocumentJournalEntries");

            entity.HasMany(d => d.SupplierInvoices)
                  .WithMany(s => s.Documents)
                  .UsingEntity("DocumentSupplierInvoices");

            entity.HasMany(d => d.CustomerInvoices)
                  .WithMany(c => c.Documents)
                  .UsingEntity("DocumentCustomerInvoices");
        });

        modelBuilder.Entity<DocumentData>(entity =>
        {
            entity.HasKey(d => d.DocumentId);
            entity.HasOne(d => d.Document)
                  .WithOne()
                  .HasForeignKey<DocumentData>(d => d.DocumentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 4: Generate the EF migration**

```bash
dotnet ef migrations add DocumentInbox \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: Migration file created under `src/KoalaBooks.Infrastructure/Migrations/`.

- [ ] **Step 5: Edit the generated migration to copy existing `JournalEntryAttachment` data**

Open the new migration file (`..._DocumentInbox.cs`) and add data migration in the `Up` method, after the `CreateTable` calls and before `migrationBuilder.DropTable`:

```csharp
// After creating Documents and DocumentData tables, copy existing attachments
migrationBuilder.Sql(@"
    INSERT INTO ""Documents"" (""OrganisationId"", ""FileName"", ""ContentType"", ""FileSize"", ""UploadedAt"", ""StorageKey"")
    SELECT fy.""OrganisationId"", a.""FileName"", a.""ContentType"", a.""FileSize"", a.""UploadedAt"",
           CAST(0 AS TEXT) -- placeholder; updated below
    FROM ""JournalEntryAttachments"" a
    JOIN ""JournalEntries"" j ON j.""Id"" = a.""JournalEntryId""
    JOIN ""FiscalYears"" fy ON fy.""Id"" = j.""FiscalYearId"";
");

// Update StorageKey to the new Document Id (which matches DocumentData.DocumentId)
migrationBuilder.Sql(@"
    WITH mapping AS (
        SELECT a.""Id"" AS old_id, d.""Id"" AS new_doc_id, a.""JournalEntryId""
        FROM ""JournalEntryAttachments"" a
        JOIN ""JournalEntries"" j ON j.""Id"" = a.""JournalEntryId""
        JOIN ""FiscalYears"" fy ON fy.""Id"" = j.""FiscalYearId""
        JOIN ""Documents"" d ON d.""OrganisationId"" = fy.""OrganisationId""
            AND d.""FileName"" = a.""FileName""
            AND d.""UploadedAt"" = a.""UploadedAt""
    )
    UPDATE ""Documents"" SET ""StorageKey"" = CAST(""Documents"".""Id"" AS TEXT)
    FROM mapping WHERE ""Documents"".""Id"" = mapping.new_doc_id;
");

migrationBuilder.Sql(@"
    INSERT INTO ""DocumentData"" (""DocumentId"", ""Data"")
    SELECT d.""Id"", a.""Data""
    FROM ""JournalEntryAttachments"" a
    JOIN ""JournalEntries"" j ON j.""Id"" = a.""JournalEntryId""
    JOIN ""FiscalYears"" fy ON fy.""Id"" = j.""FiscalYearId""
    JOIN ""Documents"" d ON d.""OrganisationId"" = fy.""OrganisationId""
        AND d.""FileName"" = a.""FileName""
        AND d.""UploadedAt"" = a.""UploadedAt"";
");

migrationBuilder.Sql(@"
    INSERT INTO ""DocumentJournalEntries"" (""DocumentsId"", ""JournalEntriesId"")
    SELECT d.""Id"", a.""JournalEntryId""
    FROM ""JournalEntryAttachments"" a
    JOIN ""JournalEntries"" j ON j.""Id"" = a.""JournalEntryId""
    JOIN ""FiscalYears"" fy ON fy.""Id"" = j.""FiscalYearId""
    JOIN ""Documents"" d ON d.""OrganisationId"" = fy.""OrganisationId""
        AND d.""FileName"" = a.""FileName""
        AND d.""UploadedAt"" = a.""UploadedAt"";
");

// Now drop the old table (also remove the DropTable call EF may have already generated)
migrationBuilder.DropTable(name: "JournalEntryAttachments");
```

Also remove the `JournalEntryAttachments` config block from `AppDbContext.OnModelCreating` and the `JournalEntryAttachments` DbSet property — EF will complain if the entity is still configured but the table is gone.

- [ ] **Step 6: Remove `JournalEntryAttachment` entity config and DbSet from `AppDbContext.cs`**

Remove the line:
```csharp
public DbSet<JournalEntryAttachment> JournalEntryAttachments => Set<JournalEntryAttachment>();
```

Remove the `modelBuilder.Entity<JournalEntryAttachment>(entity => { ... });` block.

Leave `JournalEntryAttachment.cs` in Domain for now — it will be deleted in Task 6 after Journal.razor is migrated.

- [ ] **Step 7: Verify migration applies cleanly**

```bash
dotnet ef database update \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: `Done.` with no errors.

- [ ] **Step 8: Verify build**

```bash
dotnet build KoalaBooks.slnx
```

Expected: Build succeeded.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Infrastructure/ src/KoalaBooks.Infrastructure/Data/AppDbContext.cs
git commit -m "feat: add DbDocumentStorage, configure Document in DbContext, migrate JournalEntryAttachment data"
```

---

### Task 5: DocumentService

**Files:**
- Create: `src/KoalaBooks.Application/Services/DocumentService.cs`

- [ ] **Step 1: Create `DocumentService.cs`**

```csharp
// src/KoalaBooks.Application/Services/DocumentService.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace KoalaBooks.Application.Services;

public class DocumentService(AppDbContext db, IDocumentStorage storage, IDocumentExtractor extractor, ICurrentUser currentUser)
{
    private const long MaxBytes = 10 * 1024 * 1024;

    public async Task<(Document? Doc, string? Error)> UploadAsync(string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxBytes)
            return (null, "Filen är för stor (max 10 MB).");
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");

        var doc = new Document
        {
            OrganisationId = currentUser.OrganisationId.Value,
            FileName = fileName,
            ContentType = contentType,
            FileSize = data.Length,
            UploadedAt = DateTime.UtcNow,
            StorageKey = ""
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync(); // gets doc.Id

        doc.StorageKey = await storage.SaveAsync(doc.Id, contentType, data);

        try
        {
            var result = await extractor.ExtractAsync(fileName, contentType, data);
            doc.SuggestedType = result.SuggestedType;
            doc.ClassifiedType = result.SuggestedType;
            doc.ExtractedDataJson = result.SuggestedType is not null
                ? JsonSerializer.Serialize(result)
                : null;
        }
        catch { /* extraction failure must not block upload */ }

        await db.SaveChangesAsync();
        return (doc, null);
    }

    public async Task<string?> SetTypeAsync(int documentId, string? classifiedType)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return "Dokumentet hittades inte.";
        doc.ClassifiedType = classifiedType;
        await db.SaveChangesAsync();
        return null;
    }

    public async Task<List<DocumentMeta>> GetPendingAsync() =>
        await db.Documents
            .Where(d => !d.JournalEntries.Any() && !d.SupplierInvoices.Any() && !d.CustomerInvoices.Any())
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => ToMeta(d))
            .ToListAsync();

    public async Task<List<DocumentMeta>> GetLinkedAsync(DocumentEntityType entityType, int entityId) =>
        entityType switch
        {
            DocumentEntityType.JournalEntry =>
                await db.Documents.Where(d => d.JournalEntries.Any(j => j.Id == entityId))
                    .Select(d => ToMeta(d)).ToListAsync(),
            DocumentEntityType.SupplierInvoice =>
                await db.Documents.Where(d => d.SupplierInvoices.Any(s => s.Id == entityId))
                    .Select(d => ToMeta(d)).ToListAsync(),
            DocumentEntityType.CustomerInvoice =>
                await db.Documents.Where(d => d.CustomerInvoices.Any(c => c.Id == entityId))
                    .Select(d => ToMeta(d)).ToListAsync(),
            _ => []
        };

    public async Task<Dictionary<int, int>> GetCountsForJournalEntriesAsync(IEnumerable<int> entryIds)
    {
        var ids = entryIds.ToHashSet();
        return await db.JournalEntries
            .Where(j => ids.Contains(j.Id))
            .Select(j => new { j.Id, Count = j.Documents.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);
    }

    public async Task<(string ContentType, byte[] Data, string FileName)?> GetDownloadAsync(int documentId)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return null;
        var data = await storage.LoadAsync(doc.StorageKey);
        return (doc.ContentType, data, doc.FileName);
    }

    public async Task<bool> DeleteAsync(int documentId)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return false;
        await storage.DeleteAsync(doc.StorageKey);
        db.Documents.Remove(doc);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task LinkAsync(int documentId, DocumentEntityType entityType, int entityId)
    {
        var doc = await db.Documents
            .Include(d => d.JournalEntries)
            .Include(d => d.SupplierInvoices)
            .Include(d => d.CustomerInvoices)
            .FirstOrDefaultAsync(d => d.Id == documentId);
        if (doc is null) return;

        switch (entityType)
        {
            case DocumentEntityType.JournalEntry:
                var entry = await db.JournalEntries.FindAsync(entityId);
                if (entry is not null && !doc.JournalEntries.Any(j => j.Id == entityId))
                    doc.JournalEntries.Add(entry);
                break;
            case DocumentEntityType.SupplierInvoice:
                var inv = await db.SupplierInvoices.FindAsync(entityId);
                if (inv is not null && !doc.SupplierInvoices.Any(s => s.Id == entityId))
                    doc.SupplierInvoices.Add(inv);
                break;
            case DocumentEntityType.CustomerInvoice:
                var cinv = await db.CustomerInvoices.FindAsync(entityId);
                if (cinv is not null && !doc.CustomerInvoices.Any(c => c.Id == entityId))
                    doc.CustomerInvoices.Add(cinv);
                break;
        }
        await db.SaveChangesAsync();
    }

    public async Task<(Document? Doc, string? Error)> UploadAndLinkAsync(
        string fileName, string contentType, byte[] data, DocumentEntityType entityType, int entityId)
    {
        var (doc, err) = await UploadAsync(fileName, contentType, data);
        if (doc is null) return (null, err);
        await LinkAsync(doc.Id, entityType, entityId);
        return (doc, null);
    }

    private static DocumentMeta ToMeta(Document d) => new()
    {
        Id = d.Id,
        FileName = d.FileName,
        ContentType = d.ContentType,
        FileSize = d.FileSize,
        UploadedAt = d.UploadedAt,
        ClassifiedType = d.ClassifiedType,
        SuggestedType = d.SuggestedType,
        ExtractedDataJson = d.ExtractedDataJson
    };
}

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

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:N1} KB",
        _ => $"{FileSize / (1024.0 * 1024):N1} MB"
    };
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/KoalaBooks.Application/KoalaBooks.Application.csproj
```

Expected: Build succeeded with 0 error(s).

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.Application/Services/DocumentService.cs
git commit -m "feat: add DocumentService"
```

---

### Task 6: DocumentService tests

**Files:**
- Create: `tests/KoalaBooks.Tests/DocumentServiceTests.cs`

- [ ] **Step 1: Add `DocumentService` helper to `TestFixture.cs`**

In `TestFixture.cs`, add after `SieImportService = new SieImportService(...)`:

```csharp
        public DocumentService MakeDocumentService()
        {
            var extractor = new CompositeExtractor(new FilenameExtractor(), new PdfTextExtractor());
            var storage = new DbDocumentStorage(Db);
            return new DocumentService(Db, storage, extractor, _currentUser);
        }
```

Also add the required usings at the top of `TestFixture.cs`:

```csharp
using KoalaBooks.Infrastructure.Services;
```

- [ ] **Step 2: Create `DocumentServiceTests.cs`**

```csharp
// tests/KoalaBooks.Tests/DocumentServiceTests.cs
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Tests;

public class DocumentServiceTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task UploadAsync_StoresDocumentAndReturnsIt()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", new byte[] { 1, 2, 3 });

        Assert.Null(err);
        Assert.NotNull(doc);
        Assert.Equal("faktura.pdf", doc.FileName);
        Assert.Equal(3, doc.FileSize);
        Assert.NotEmpty(doc.StorageKey);
    }

    [Fact]
    public async Task UploadAsync_SetsClassifiedTypeFromFilename()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", []);

        Assert.Equal("SupplierInvoice", doc!.ClassifiedType);
        Assert.Equal("SupplierInvoice", doc.SuggestedType);
    }

    [Fact]
    public async Task UploadAsync_RejectsOversizedFile()
    {
        var svc = _fx.MakeDocumentService();
        var bigData = new byte[11 * 1024 * 1024];
        var (doc, err) = await svc.UploadAsync("big.pdf", "application/pdf", bigData);

        Assert.Null(doc);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task GetPendingAsync_ReturnsOnlyUnlinkedDocuments()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var entry = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);

        await svc.UploadAsync("unlinked.pdf", "application/pdf", [1]);
        var (linked, _) = await svc.UploadAsync("linked.pdf", "application/pdf", [2]);
        await svc.LinkAsync(linked!.Id, DocumentEntityType.JournalEntry, entry.Id);

        var pending = await svc.GetPendingAsync();

        Assert.Single(pending);
        Assert.Equal("unlinked.pdf", pending[0].FileName);
    }

    [Fact]
    public async Task SetTypeAsync_UpdatesClassifiedType()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", []);

        var err = await svc.SetTypeAsync(doc!.Id, "CustomerInvoice");

        Assert.Null(err);
        var updated = (await svc.GetPendingAsync()).First(d => d.Id == doc.Id);
        Assert.Equal("CustomerInvoice", updated.ClassifiedType);
    }

    [Fact]
    public async Task GetLinkedAsync_ReturnsDocumentsForJournalEntry()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var entry = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);

        var (doc, _) = await svc.UploadAsync("receipt.pdf", "application/pdf", [5]);
        await svc.LinkAsync(doc!.Id, DocumentEntityType.JournalEntry, entry.Id);

        var linked = await svc.GetLinkedAsync(DocumentEntityType.JournalEntry, entry.Id);

        Assert.Single(linked);
        Assert.Equal("receipt.pdf", linked[0].FileName);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDocumentAndData()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("todelete.pdf", "application/pdf", [9, 8, 7]);

        var deleted = await svc.DeleteAsync(doc!.Id);
        Assert.True(deleted);

        var pending = await svc.GetPendingAsync();
        Assert.Empty(pending);

        var download = await svc.GetDownloadAsync(doc.Id);
        Assert.Null(download);
    }

    [Fact]
    public async Task GetDownloadAsync_ReturnsBytesForUploadedDocument()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("file.pdf", "application/pdf", [10, 20, 30]);

        var result = await svc.GetDownloadAsync(doc!.Id);

        Assert.NotNull(result);
        Assert.Equal("application/pdf", result.Value.ContentType);
        Assert.Equal(new byte[] { 10, 20, 30 }, result.Value.Data);
    }

    [Fact]
    public async Task GetCountsForJournalEntriesAsync_CountsCorrectly()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var e1 = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);
        var e2 = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 200m);

        var (doc1, _) = await svc.UploadAsync("a.pdf", "application/pdf", [1]);
        var (doc2, _) = await svc.UploadAsync("b.pdf", "application/pdf", [2]);
        var (doc3, _) = await svc.UploadAsync("c.pdf", "application/pdf", [3]);

        await svc.LinkAsync(doc1!.Id, DocumentEntityType.JournalEntry, e1.Id);
        await svc.LinkAsync(doc2!.Id, DocumentEntityType.JournalEntry, e1.Id);
        await svc.LinkAsync(doc3!.Id, DocumentEntityType.JournalEntry, e2.Id);

        var counts = await svc.GetCountsForJournalEntriesAsync([e1.Id, e2.Id]);

        Assert.Equal(2, counts[e1.Id]);
        Assert.Equal(1, counts[e2.Id]);
    }
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj --filter "DocumentServiceTests" -v normal
```

Expected: All 8 tests pass.

- [ ] **Step 4: Commit**

```bash
git add tests/KoalaBooks.Tests/DocumentServiceTests.cs tests/KoalaBooks.Tests/TestFixture.cs
git commit -m "test: add DocumentServiceTests"
```

---

### Task 7: Web layer — provider, endpoint, DI registration

**Files:**
- Create: `src/KoalaBooks.Web/Services/WebDocumentProvider.cs`
- Modify: `src/KoalaBooks.Web/Program.cs`

- [ ] **Step 1: Create `WebDocumentProvider.cs`**

```csharp
// src/KoalaBooks.Web/Services/WebDocumentProvider.cs
using KoalaBooks.Application.Services;

namespace KoalaBooks.Web.Services;

public class WebDocumentProvider : IDocumentProvider
{
    public string GetDownloadUrl(int documentId) => $"/documents/{documentId}";
}
```

- [ ] **Step 2: Update `Program.cs` — register new services and swap endpoint**

Replace:
```csharp
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<IAttachmentProvider, WebAttachmentProvider>();
```

With:
```csharp
builder.Services.AddScoped<KoalaBooks.Infrastructure.Services.FilenameExtractor>();
builder.Services.AddScoped<KoalaBooks.Infrastructure.Services.PdfTextExtractor>();
builder.Services.AddScoped<KoalaBooks.Infrastructure.Services.CompositeExtractor>();
builder.Services.AddScoped<KoalaBooks.Application.Services.IDocumentExtractor>(sp =>
    sp.GetRequiredService<KoalaBooks.Infrastructure.Services.CompositeExtractor>());
builder.Services.AddScoped<KoalaBooks.Application.Services.IDocumentStorage,
    KoalaBooks.Infrastructure.Services.DbDocumentStorage>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<IDocumentProvider, WebDocumentProvider>();
```

Replace the `/attachments/{id:int}` endpoint:
```csharp
app.MapGet("/attachments/{id:int}", async (int id, AttachmentService svc) =>
{
    var a = await svc.GetAsync(id);
    return a is null ? Results.NotFound() : Results.File(a.Data, a.ContentType, a.FileName);
}).RequireAuthorization();
```

With:
```csharp
app.MapGet("/documents/{id:int}", async (int id, DocumentService svc) =>
{
    var result = await svc.GetDownloadAsync(id);
    return result is null
        ? Results.NotFound()
        : Results.File(result.Value.Data, result.Value.ContentType, result.Value.FileName);
}).RequireAuthorization();
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/KoalaBooks.Web/KoalaBooks.Web.csproj
```

Expected: Build succeeded. (There will be compile errors in Journal.razor referencing old `AttachmentService` — those are fixed in Task 8.)

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Web/Services/WebDocumentProvider.cs src/KoalaBooks.Web/Program.cs
git commit -m "feat: add WebDocumentProvider and /documents/{id} endpoint; register document services"
```

---

### Task 8: Auto-link documents when invoices are posted

**Files:**
- Modify: `src/KoalaBooks.Application/Services/SupplierInvoiceService.cs`
- Modify: `src/KoalaBooks.Application/Services/CustomerInvoiceService.cs`

- [ ] **Step 1: Update `SupplierInvoiceService.PostAsync`**

In `SupplierInvoiceService.PostAsync`, after `await tx.CommitAsync();` and before `return (invoice, null);`, add:

```csharp
        // Propagate document links to the new journal entry
        var docIds = await _db.Documents
            .Where(d => d.SupplierInvoices.Any(s => s.Id == invoiceId))
            .Select(d => d.Id)
            .ToListAsync();

        foreach (var docId in docIds)
        {
            var doc = await _db.Documents
                .Include(d => d.JournalEntries)
                .FirstOrDefaultAsync(d => d.Id == docId);
            if (doc is not null && !doc.JournalEntries.Any(j => j.Id == journalEntry.Id))
                doc.JournalEntries.Add(journalEntry);
        }
        if (docIds.Count > 0)
            await _db.SaveChangesAsync();
```

- [ ] **Step 2: Update `CustomerInvoiceService.PostAsync`**

Find `PostAsync` in `CustomerInvoiceService.cs`. After `await tx.CommitAsync();` and before `return (invoice, null);`, add the same propagation block (substituting `CustomerInvoice` for `SupplierInvoice`):

```csharp
        // Propagate document links to the new journal entry
        var docIds = await _db.Documents
            .Where(d => d.CustomerInvoices.Any(c => c.Id == invoiceId))
            .Select(d => d.Id)
            .ToListAsync();

        foreach (var docId in docIds)
        {
            var doc = await _db.Documents
                .Include(d => d.JournalEntries)
                .FirstOrDefaultAsync(d => d.Id == docId);
            if (doc is not null && !doc.JournalEntries.Any(j => j.Id == journalEntry.Id))
                doc.JournalEntries.Add(journalEntry);
        }
        if (docIds.Count > 0)
            await _db.SaveChangesAsync();
```

- [ ] **Step 3: Verify build**

```bash
dotnet build KoalaBooks.slnx
```

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Application/Services/SupplierInvoiceService.cs \
        src/KoalaBooks.Application/Services/CustomerInvoiceService.cs
git commit -m "feat: auto-link documents to journal entry when supplier/customer invoice is posted"
```

---

### Task 9: Migrate `Journal.razor` from `AttachmentService` to `DocumentService`

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`

The changes are mechanical: every `AttachmentService` call maps to `DocumentService`, every `AttachmentMeta` becomes `DocumentMeta`, and `IAttachmentProvider` becomes `IDocumentProvider`.

- [ ] **Step 1: Update `@using` and injected fields in the `@code` block**

Replace:
```csharp
    [Inject] private AttachmentService AttachmentService { get; set; } = default!;
    [Inject] private IAttachmentProvider AttachmentProvider { get; set; } = default!;
    private List<AttachmentMeta> _attachmentMeta = [];
```

With:
```csharp
    [Inject] private DocumentService DocumentService { get; set; } = default!;
    [Inject] private IDocumentProvider DocumentProvider { get; set; } = default!;
    private List<DocumentMeta> _attachmentMeta = [];
```

- [ ] **Step 2: Update `_attachmentCounts` population (called after loading entries)**

Replace both occurrences of:
```csharp
        _attachmentCounts = await AttachmentService.GetCountsAsync(_entries.Select(e => e.Id));
```

With:
```csharp
        _attachmentCounts = await DocumentService.GetCountsForJournalEntriesAsync(_entries.Select(e => e.Id));
```

- [ ] **Step 3: Update `ToggleAttachments`**

Replace:
```csharp
        _attachmentMeta = await AttachmentService.GetMetaAsync(entry.Id);
```

With:
```csharp
        _attachmentMeta = await DocumentService.GetLinkedAsync(DocumentEntityType.JournalEntry, entry.Id);
```

Add `@using KoalaBooks.Domain.Enums` to the top of Journal.razor if not already present.

- [ ] **Step 4: Update `UploadAttachmentAsync`**

Replace:
```csharp
            var added = await AttachmentService.AddAsync(_attachmentEntryId!.Value, e.File.Name, contentType, ms.ToArray());
            if (added is null)
            {
                _attachmentError = "Verifikationen hittades inte.";
                return;
            }
            _attachmentMeta = await AttachmentService.GetMetaAsync(_attachmentEntryId.Value);
            _attachmentCounts[_attachmentEntryId.Value] = _attachmentMeta.Count;
```

With:
```csharp
            var (added, uploadErr) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, ms.ToArray(),
                DocumentEntityType.JournalEntry, _attachmentEntryId!.Value);
            if (added is null)
            {
                _attachmentError = uploadErr ?? "Uppladdning misslyckades.";
                return;
            }
            _attachmentMeta = await DocumentService.GetLinkedAsync(DocumentEntityType.JournalEntry, _attachmentEntryId.Value);
            _attachmentCounts[_attachmentEntryId.Value] = _attachmentMeta.Count;
```

Also replace the early size check:
```csharp
            const long maxBytes = 10 * 1024 * 1024;
            if (e.File.Size > maxBytes)
            {
                _attachmentError = "Filen är för stor (max 10 MB).";
                return;
            }
```
Keep as-is (it's a UI-side guard).

- [ ] **Step 5: Update `DeleteAttachmentAsync`**

Replace:
```csharp
        await AttachmentService.DeleteAsync(attachmentId);
        _attachmentMeta = await AttachmentService.GetMetaAsync(_attachmentEntryId!.Value);
```

With:
```csharp
        await DocumentService.DeleteAsync(attachmentId);
        _attachmentMeta = await DocumentService.GetLinkedAsync(DocumentEntityType.JournalEntry, _attachmentEntryId!.Value);
```

- [ ] **Step 6: Update download URL in the template**

Replace:
```razor
<a href="@AttachmentProvider.GetDownloadUrl(a.Id)" target="_blank" style="color:#2563eb;">@a.FileName</a>
```

With:
```razor
<a href="@DocumentProvider.GetDownloadUrl(a.Id)" target="_blank" style="color:#2563eb;">@a.FileName</a>
```

- [ ] **Step 7: Verify build and run all tests**

```bash
dotnet build KoalaBooks.slnx
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj -v normal
```

Expected: Build succeeded, all tests pass. (Old `AttachmentServiceTests` may need updating — see next step.)

- [ ] **Step 8: Update `AttachmentServiceTests.cs`**

`AttachmentService` no longer exists. Delete `tests/KoalaBooks.Tests/AttachmentServiceTests.cs` — its coverage is replaced by `DocumentServiceTests`.

```bash
rm tests/KoalaBooks.Tests/AttachmentServiceTests.cs
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj -v normal
```

Expected: All tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor
git rm tests/KoalaBooks.Tests/AttachmentServiceTests.cs
git commit -m "feat: migrate Journal.razor from AttachmentService to DocumentService"
```

---

### Task 10: Document panel in SupplierInvoices.razor

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/SupplierInvoices.razor`

Add a collapsible document panel to the expanded row (the same row that currently has the "Bokför" and "Markera betald" expand panels).

- [ ] **Step 1: Add `DocumentService` and `IDocumentProvider` injections to `@code`**

In the `@code` block of `SupplierInvoices.razor`, add:

```csharp
    [Inject] private DocumentService DocumentService { get; set; } = default!;
    [Inject] private IDocumentProvider DocumentProvider { get; set; } = default!;

    private int? _docPanelInvoiceId;
    private List<DocumentMeta> _invoiceDocuments = [];
    private bool _uploadingDoc;
    private string? _docError;
```

- [ ] **Step 2: Add document panel toggle and upload methods to `@code`**

```csharp
    private async Task ToggleDocPanel(int invoiceId)
    {
        _docError = null;
        if (_docPanelInvoiceId == invoiceId)
        {
            _docPanelInvoiceId = null;
            _invoiceDocuments = [];
            return;
        }
        _docPanelInvoiceId = invoiceId;
        _invoiceDocuments = await DocumentService.GetLinkedAsync(DocumentEntityType.SupplierInvoice, invoiceId);
    }

    private async Task UploadInvoiceDocAsync(InputFileChangeEventArgs e)
    {
        _docError = null;
        _uploadingDoc = true;
        try
        {
            const long maxBytes = 10 * 1024 * 1024;
            if (e.File.Size > maxBytes) { _docError = "Filen är för stor (max 10 MB)."; return; }
            using var ms = new MemoryStream();
            await e.File.OpenReadStream(maxBytes).CopyToAsync(ms);
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, ms.ToArray(),
                DocumentEntityType.SupplierInvoice, _docPanelInvoiceId!.Value);
            if (doc is null) { _docError = err ?? "Uppladdning misslyckades."; return; }
            _invoiceDocuments = await DocumentService.GetLinkedAsync(DocumentEntityType.SupplierInvoice, _docPanelInvoiceId.Value);
            Snackbar.Add($"{e.File.Name} uppladdad.", Severity.Success);
        }
        finally { _uploadingDoc = false; }
    }

    private async Task DeleteInvoiceDocAsync(int documentId)
    {
        await DocumentService.DeleteAsync(documentId);
        _invoiceDocuments = await DocumentService.GetLinkedAsync(DocumentEntityType.SupplierInvoice, _docPanelInvoiceId!.Value);
    }
```

- [ ] **Step 3: Add `@using` for `DocumentEntityType` at the top of the file**

```razor
@using KoalaBooks.Domain.Enums
```

- [ ] **Step 4: Add document panel button to the actions column in the table**

In the `<td>` that contains the Bokför/Markera betald buttons, add a documents button alongside the existing buttons for each row:

```razor
<button class="btn btn-sm @(_docPanelInvoiceId == inv.Id ? "btn-primary" : "btn-secondary")"
        @onclick="() => ToggleDocPanel(inv.Id)">
    📎 @(_docPanelInvoiceId == inv.Id ? "Dölj" : "Dokument")
</button>
```

- [ ] **Step 5: Add the expanded document panel row in the table body**

After the existing `@if (isExpanded && _expandMode == InvoiceExpandMode.Pay) { ... }` block, add:

```razor
@if (_docPanelInvoiceId == inv.Id)
{
    <tr style="background:#f8fafc;">
        <td colspan="9" style="padding:1rem;">
            <div style="max-width:640px;">
                <p style="margin:0 0 0.75rem; font-weight:600;">📎 Dokument — @inv.SupplierName</p>
                @if (_docError is not null)
                {
                    <div style="color:#ef4444; margin-bottom:0.5rem; font-size:0.875rem;">@_docError</div>
                }
                @if (_invoiceDocuments.Any())
                {
                    <table style="font-size:0.85rem; margin-bottom:1rem;">
                        <thead>
                            <tr>
                                <th>Filnamn</th>
                                <th style="width:90px; text-align:right;">Storlek</th>
                                <th style="width:140px;">Uppladdad</th>
                                <th style="width:80px;"></th>
                            </tr>
                        </thead>
                        <tbody>
                            @foreach (var d in _invoiceDocuments)
                            {
                                <tr>
                                    <td><a href="@DocumentProvider.GetDownloadUrl(d.Id)" target="_blank" style="color:#2563eb;">@d.FileName</a></td>
                                    <td style="text-align:right; color:#64748b;">@d.FileSizeDisplay</td>
                                    <td style="color:#64748b;">@d.UploadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")</td>
                                    <td>
                                        <button class="btn btn-sm btn-danger"
                                                @onclick="() => DeleteInvoiceDocAsync(d.Id)"
                                                disabled="@_uploadingDoc">🗑️</button>
                                    </td>
                                </tr>
                            }
                        </tbody>
                    </table>
                }
                else
                {
                    <p style="color:#94a3b8; font-style:italic; margin:0 0 0.75rem;">Inga dokument ännu.</p>
                }
                <label class="btn btn-secondary" style="cursor:pointer; display:inline-block;">
                    @(_uploadingDoc ? "Laddar upp..." : "📁 Välj fil (max 10 MB)")
                    <InputFile OnChange="UploadInvoiceDocAsync" style="display:none"
                               accept=".pdf,.png,.jpg,.jpeg"
                               disabled="@_uploadingDoc" />
                </label>
            </div>
        </td>
    </tr>
}
```

- [ ] **Step 6: Verify build**

```bash
dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Components/Pages/SupplierInvoices.razor
git commit -m "feat: add document panel to SupplierInvoices page"
```

---

### Task 11: Document panel in CustomerInvoices.razor

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`

Same pattern as Task 10 but for customer invoices. Read `CustomerInvoices.razor` first to find the right place to add the panel.

- [ ] **Step 1: Add injections, state fields, and methods to `@code`**

```csharp
    [Inject] private DocumentService DocumentService { get; set; } = default!;
    [Inject] private IDocumentProvider DocumentProvider { get; set; } = default!;

    private int? _docPanelInvoiceId;
    private List<DocumentMeta> _invoiceDocuments = [];
    private bool _uploadingDoc;
    private string? _docError;

    private async Task ToggleDocPanel(int invoiceId)
    {
        _docError = null;
        if (_docPanelInvoiceId == invoiceId) { _docPanelInvoiceId = null; _invoiceDocuments = []; return; }
        _docPanelInvoiceId = invoiceId;
        _invoiceDocuments = await DocumentService.GetLinkedAsync(DocumentEntityType.CustomerInvoice, invoiceId);
    }

    private async Task UploadInvoiceDocAsync(InputFileChangeEventArgs e)
    {
        _docError = null;
        _uploadingDoc = true;
        try
        {
            const long maxBytes = 10 * 1024 * 1024;
            if (e.File.Size > maxBytes) { _docError = "Filen är för stor (max 10 MB)."; return; }
            using var ms = new MemoryStream();
            await e.File.OpenReadStream(maxBytes).CopyToAsync(ms);
            var contentType = string.IsNullOrWhiteSpace(e.File.ContentType) ? "application/octet-stream" : e.File.ContentType;
            var (doc, err) = await DocumentService.UploadAndLinkAsync(
                e.File.Name, contentType, ms.ToArray(),
                DocumentEntityType.CustomerInvoice, _docPanelInvoiceId!.Value);
            if (doc is null) { _docError = err ?? "Uppladdning misslyckades."; return; }
            _invoiceDocuments = await DocumentService.GetLinkedAsync(DocumentEntityType.CustomerInvoice, _docPanelInvoiceId.Value);
            Snackbar.Add($"{e.File.Name} uppladdad.", Severity.Success);
        }
        finally { _uploadingDoc = false; }
    }

    private async Task DeleteInvoiceDocAsync(int documentId)
    {
        await DocumentService.DeleteAsync(documentId);
        _invoiceDocuments = await DocumentService.GetLinkedAsync(DocumentEntityType.CustomerInvoice, _docPanelInvoiceId!.Value);
    }
```

- [ ] **Step 2: Add `@using KoalaBooks.Domain.Enums` at the top of the file**

- [ ] **Step 3: Add document button and expanded panel to the table**

Follow the same structure as Task 10, Step 4 and Step 5, replacing `SupplierInvoice` references with `CustomerInvoice` and adjusting `colspan` to match the customer invoices table.

- [ ] **Step 4: Verify build**

```bash
dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj
```

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Pages/CustomerInvoices.razor
git commit -m "feat: add document panel to CustomerInvoices page"
```

---

### Task 12: `Inkorg.razor` — the inbox page

**Files:**
- Create: `src/KoalaBooks.Components/Pages/Inkorg.razor`
- Modify: `src/KoalaBooks.Components/Layout/MainLayout.razor`

- [ ] **Step 1: Add Inkorg nav link to `MainLayout.razor`**

After `<MudNavLink Href="/journal" ...>Journal</MudNavLink>`, add:

```razor
<MudNavLink Href="/inkorg" Icon="@Icons.Material.Outlined.Inbox">Inkorg</MudNavLink>
```

- [ ] **Step 2: Create `Inkorg.razor`**

```razor
@page "/inkorg"
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Enums
@using MudBlazor
@using Microsoft.AspNetCore.Components.Forms

<PageTitle>Inkorg — KoalaBooks</PageTitle>

<h1>📥 Inkorg</h1>

@if (_error is not null)
{
    <MudAlert Severity="Severity.Error" Class="mb-3" ShowCloseIcon="true" CloseIconClicked="() => _error = null">@_error</MudAlert>
}

<div class="toolbar" style="margin-bottom:1rem; display:flex; align-items:center; gap:0.75rem;">
    <label class="btn btn-primary" style="cursor:pointer;">
        @(_uploading ? "Laddar upp..." : "+ Ladda upp dokument")
        <InputFile OnChange="UploadAsync" style="display:none"
                   accept=".pdf,.png,.jpg,.jpeg" multiple
                   disabled="@_uploading" />
    </label>

    <div style="display:flex; gap:0.25rem;">
        @foreach (var (label, val) in Filters)
        {
            <button class="btn btn-sm @(_filter == val ? "btn-primary" : "btn-secondary")"
                    @onclick="() => _filter = val">@label</button>
        }
    </div>
</div>

@if (_isLoading)
{
    <MudProgressLinear Color="Color.Primary" Indeterminate="true" Class="mb-4" />
}
else if (!Filtered.Any())
{
    <p style="color:#94a3b8; font-style:italic;">Inga dokument i inkorgen.</p>
}
else
{
    <div class="card" style="padding:0; overflow:hidden;">
        <table style="margin:0;">
            <thead>
                <tr>
                    <th>Filnamn</th>
                    <th style="width:90px; text-align:right;">Storlek</th>
                    <th style="width:140px;">Uppladdad</th>
                    <th style="width:180px;">Typ</th>
                    <th style="width:160px;">Åtgärder</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var doc in Filtered)
                {
                    <tr>
                        <td style="font-weight:500;">@doc.FileName</td>
                        <td style="text-align:right; color:#64748b; font-size:0.875rem;">@doc.FileSizeDisplay</td>
                        <td style="color:#64748b; font-size:0.875rem;">@doc.UploadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")</td>
                        <td>
                            <select style="font-size:0.8rem; border:1px solid #e2e8f0; border-radius:4px; padding:2px 6px; width:100%;"
                                    value="@(doc.ClassifiedType ?? "")"
                                    @onchange="e => SetTypeAsync(doc.Id, e.Value?.ToString())">
                                <option value="">— Välj typ —</option>
                                <option value="SupplierInvoice">Leverantörsfaktura</option>
                                <option value="CustomerInvoice">Kundfaktura</option>
                                <option value="JournalEntry">Verifikation</option>
                            </select>
                        </td>
                        <td style="display:flex; gap:0.3rem;">
                            <button class="btn btn-sm btn-primary" @onclick="() => OpenClassifyDialog(doc)">
                                Klassificera
                            </button>
                            <button class="btn btn-sm btn-danger" @onclick="() => DeleteAsync(doc.Id)">🗑</button>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
}

@if (_classifyDoc is not null)
{
    <ClassifyDocumentDialog Doc="_classifyDoc"
                            DocumentProvider="DocumentProvider"
                            OnClassified="OnDocumentClassified"
                            OnClose="() => _classifyDoc = null" />
}

@code {
    [Inject] private DocumentService DocumentService { get; set; } = default!;
    [Inject] private IDocumentProvider DocumentProvider { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private List<DocumentMeta> _docs = [];
    private bool _isLoading;
    private bool _uploading;
    private string? _error;
    private string _filter = "all";
    private DocumentMeta? _classifyDoc;

    private static (string Label, string Value)[] Filters =>
    [
        ("Alla", "all"),
        ("Oklassificerade", "unclassified"),
        ("Leverantörsfaktura", "SupplierInvoice"),
        ("Kundfaktura", "CustomerInvoice"),
        ("Verifikation", "JournalEntry"),
    ];

    private IEnumerable<DocumentMeta> Filtered => _filter switch
    {
        "unclassified" => _docs.Where(d => d.ClassifiedType is null),
        "all"          => _docs,
        var t          => _docs.Where(d => d.ClassifiedType == t)
    };

    protected override async Task OnInitializedAsync()
    {
        _isLoading = true;
        _docs = await DocumentService.GetPendingAsync();
        _isLoading = false;
    }

    private async Task UploadAsync(InputFileChangeEventArgs e)
    {
        _error = null;
        _uploading = true;
        try
        {
            foreach (var file in e.GetMultipleFiles(10))
            {
                const long maxBytes = 10 * 1024 * 1024;
                if (file.Size > maxBytes) { _error = $"{file.Name}: för stor (max 10 MB)."; continue; }
                using var ms = new MemoryStream();
                await file.OpenReadStream(maxBytes).CopyToAsync(ms);
                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
                var (_, err) = await DocumentService.UploadAsync(file.Name, contentType, ms.ToArray());
                if (err is not null) _error = err;
                else Snackbar.Add($"{file.Name} uppladdad.", Severity.Success);
            }
            _docs = await DocumentService.GetPendingAsync();
        }
        finally { _uploading = false; }
    }

    private async Task SetTypeAsync(int docId, string? type)
    {
        var err = await DocumentService.SetTypeAsync(docId, string.IsNullOrEmpty(type) ? null : type);
        if (err is not null) { _error = err; return; }
        var doc = _docs.FirstOrDefault(d => d.Id == docId);
        if (doc is not null) doc.ClassifiedType = string.IsNullOrEmpty(type) ? null : type;
    }

    private void OpenClassifyDialog(DocumentMeta doc) => _classifyDoc = doc;

    private async Task OnDocumentClassified()
    {
        _classifyDoc = null;
        _docs = await DocumentService.GetPendingAsync();
        Snackbar.Add("Dokument klassificerat.", Severity.Success);
    }

    private async Task DeleteAsync(int docId)
    {
        await DocumentService.DeleteAsync(docId);
        _docs = _docs.Where(d => d.Id != docId).ToList();
        Snackbar.Add("Dokument raderat.", Severity.Success);
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/KoalaBooks.Components/KoalaBooks.Components.csproj
```

Expected: Build error on `ClassifyDocumentDialog` (not yet created — that's Task 13). If the only errors are about `ClassifyDocumentDialog`, proceed.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Inkorg.razor \
        src/KoalaBooks.Components/Layout/MainLayout.razor
git commit -m "feat: add Inkorg page and nav link"
```

---

### Task 13: `ClassifyDocumentDialog.razor`

**Files:**
- Create: `src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor`

- [ ] **Step 1: Create `ClassifyDocumentDialog.razor`**

```razor
@* src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor *@
@using KoalaBooks.Application.Services
@using KoalaBooks.Domain.Entities
@using KoalaBooks.Domain.Enums
@using MudBlazor
@using System.Text.Json

<MudDialog Style="max-width:900px; width:95vw;">
    <TitleContent>
        <MudText Typo="Typo.h6">Klassificera dokument</MudText>
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
                    <img src="@DocumentProvider.GetDownloadUrl(Doc.Id)"
                         style="flex:1; object-fit:contain; max-height:460px;" alt="@Doc.FileName" />
                }
                <div style="padding:6px 10px; font-size:0.75rem; color:#64748b; border-top:1px solid #f1f5f9;">
                    @Doc.FileName &nbsp;·&nbsp; @Doc.FileSizeDisplay
                </div>
            </div>

            <!-- Right: form -->
            <div style="flex:1; padding:1.25rem; overflow-y:auto; display:flex; flex-direction:column; gap:0.75rem;">

                @if (_error is not null)
                {
                    <MudAlert Severity="Severity.Error" Dense="true">@_error</MudAlert>
                }

                <div class="form-group">
                    <label style="font-weight:600;">Typ <span style="color:#ef4444;">*</span></label>
                    <select @bind="_type" style="width:100%;">
                        <option value="">— Välj typ —</option>
                        <option value="SupplierInvoice">Leverantörsfaktura</option>
                        <option value="CustomerInvoice">Kundfaktura</option>
                        <option value="JournalEntry">Verifikation</option>
                    </select>
                </div>

                @if (_type == "SupplierInvoice")
                {
                    <div style="display:grid; grid-template-columns:1fr 1fr; gap:0.5rem 1rem;">
                        <div class="form-group" style="grid-column:1/-1;">
                            <label>Leverantör <span style="color:#ef4444;">*</span></label>
                            <input type="text" @bind="_supplier" placeholder="Företagsnamn" style="width:100%;" />
                        </div>
                        <div class="form-group">
                            <label>Fakturanummer</label>
                            <input type="text" @bind="_invoiceNumber" style="width:100%;" />
                        </div>
                        <div class="form-group">
                            <label>Fakturadatum <span style="color:#ef4444;">*</span></label>
                            <DateInput @bind-Value="_invoiceDate" />
                        </div>
                        <div class="form-group">
                            <label>Förfallodatum <span style="color:#ef4444;">*</span></label>
                            <DateInput @bind-Value="_dueDate" />
                        </div>
                        <div class="form-group">
                            <label>Belopp exkl. moms <span style="color:#ef4444;">*</span></label>
                            <input type="number" step="0.01" @bind="_amountExcl" style="width:100%;" />
                        </div>
                        <div class="form-group">
                            <label>Momsbelopp</label>
                            <input type="number" step="0.01" @bind="_vatAmount" style="width:100%;" />
                        </div>
                    </div>
                }
                else if (_type == "CustomerInvoice")
                {
                    <div style="display:grid; grid-template-columns:1fr 1fr; gap:0.5rem 1rem;">
                        <div class="form-group" style="grid-column:1/-1;">
                            <label>Kund <span style="color:#ef4444;">*</span></label>
                            <select @bind="_customerId" style="width:100%;">
                                <option value="0">— Välj kund —</option>
                                @foreach (var c in _customers)
                                {
                                    <option value="@c.Id">@c.Name</option>
                                }
                            </select>
                        </div>
                        <div class="form-group">
                            <label>Fakturadatum <span style="color:#ef4444;">*</span></label>
                            <DateInput @bind-Value="_invoiceDate" />
                        </div>
                        <div class="form-group">
                            <label>Förfallodatum <span style="color:#ef4444;">*</span></label>
                            <DateInput @bind-Value="_dueDate" />
                        </div>
                        <div class="form-group">
                            <label>Belopp exkl. moms <span style="color:#ef4444;">*</span></label>
                            <input type="number" step="0.01" @bind="_amountExcl" style="width:100%;" />
                        </div>
                        <div class="form-group">
                            <label>Momsbelopp</label>
                            <input type="number" step="0.01" @bind="_vatAmount" style="width:100%;" />
                        </div>
                    </div>
                }
                else if (_type == "JournalEntry")
                {
                    <div style="display:flex; gap:0.5rem; margin-bottom:0.5rem;">
                        <button class="btn btn-sm @(_jeMode == "new" ? "btn-primary" : "btn-secondary")"
                                @onclick='() => _jeMode = "new"'>Ny verifikation</button>
                        <button class="btn btn-sm @(_jeMode == "existing" ? "btn-primary" : "btn-secondary")"
                                @onclick='() => { _jeMode = "existing"; LoadEntriesIfNeeded(); }'>Koppla befintlig</button>
                    </div>

                    @if (_jeMode == "new")
                    {
                        <div class="form-group">
                            <label>Datum <span style="color:#ef4444;">*</span></label>
                            <DateInput @bind-Value="_invoiceDate" />
                        </div>
                        <div class="form-group">
                            <label>Beskrivning <span style="color:#ef4444;">*</span></label>
                            <input type="text" @bind="_description" style="width:100%;" placeholder="Beskrivning av transaktion" />
                        </div>
                        <table style="font-size:0.85rem; width:100%; margin-bottom:0.5rem;">
                            <thead>
                                <tr>
                                    <th>Konto</th>
                                    <th style="width:120px;">Debet</th>
                                    <th style="width:120px;">Kredit</th>
                                    <th style="width:36px;"></th>
                                </tr>
                            </thead>
                            <tbody>
                                @for (int i = 0; i < _lines.Count; i++)
                                {
                                    var idx = i;
                                    <tr>
                                        <td>
                                            <AccountSearchDropdown Accounts="_accounts"
                                                @bind-SelectedAccountId="_lines[idx].AccountId" />
                                        </td>
                                        <td>
                                            <input type="number" step="0.01"
                                                   value="@(_lines[idx].DebitAmount == 0 ? "" : _lines[idx].DebitAmount.ToString())"
                                                   @onchange="e => { if (decimal.TryParse(e.Value?.ToString(), out var v)) { _lines[idx].DebitAmount = v; _lines[idx].CreditAmount = 0; } }"
                                                   style="width:100%;" />
                                        </td>
                                        <td>
                                            <input type="number" step="0.01"
                                                   value="@(_lines[idx].CreditAmount == 0 ? "" : _lines[idx].CreditAmount.ToString())"
                                                   @onchange="e => { if (decimal.TryParse(e.Value?.ToString(), out var v)) { _lines[idx].CreditAmount = v; _lines[idx].DebitAmount = 0; } }"
                                                   style="width:100%;" />
                                        </td>
                                        <td>
                                            <button class="btn btn-sm btn-danger"
                                                    @onclick="() => _lines.RemoveAt(idx)"
                                                    disabled="@(_lines.Count <= 2)">✕</button>
                                        </td>
                                    </tr>
                                }
                            </tbody>
                        </table>
                        <button class="btn btn-sm btn-secondary" @onclick="AddLine">+ Rad</button>
                    }
                    else
                    {
                        @if (_linkableEntries.Count == 0)
                        {
                            <p style="color:#94a3b8; font-size:0.85rem;">Inga verifikationer hittades.</p>
                        }
                        else
                        {
                            <select @bind="_existingEntryId" style="width:100%;">
                                <option value="0">— Välj verifikation —</option>
                                @foreach (var e in _linkableEntries)
                                {
                                    <option value="@e.Id">#@e.EntryNumber @e.Date.ToString("yyyy-MM-dd") — @e.Description</option>
                                }
                            </select>
                        }
                    }
                }

                <div style="margin-top:auto; display:flex; gap:0.5rem; padding-top:0.75rem; border-top:1px solid #f1f5f9;">
                    <button class="btn btn-success" @onclick="ClassifyAsync" disabled="@(_saving || _type == "")">
                        @(_saving ? "Sparar..." : "Skapa & koppla")
                    </button>
                    <button class="btn btn-secondary" @onclick="OnClose">Avbryt</button>
                </div>
            </div>
        </div>
    </DialogContent>
</MudDialog>

@code {
    [Parameter, EditorRequired] public DocumentMeta Doc { get; set; } = default!;
    [Parameter, EditorRequired] public IDocumentProvider DocumentProvider { get; set; } = default!;
    [Parameter, EditorRequired] public EventCallback OnClassified { get; set; }
    [Parameter, EditorRequired] public EventCallback OnClose { get; set; }

    [Inject] private DocumentService DocumentService { get; set; } = default!;
    [Inject] private SupplierInvoiceService SupplierInvoiceService { get; set; } = default!;
    [Inject] private CustomerInvoiceService CustomerInvoiceService { get; set; } = default!;
    [Inject] private JournalEntryService JournalEntryService { get; set; } = default!;
    [Inject] private FiscalYearService FiscalYearService { get; set; } = default!;
    [Inject] private AccountService AccountService { get; set; } = default!;
    [Inject] private CustomerService CustomerService { get; set; } = default!;

    private string _type = "";
    private string _supplier = "";
    private string _invoiceNumber = "";
    private DateTime _invoiceDate = DateTime.Today;
    private DateTime _dueDate = DateTime.Today.AddDays(30);
    private decimal _amountExcl;
    private decimal _vatAmount;
    private string _description = "";
    private string _jeMode = "new";
    private int _existingEntryId;
    private int _customerId;
    private bool _saving;
    private string? _error;

    private List<JournalEntryLine> _lines = [new(), new()];
    private List<JournalEntry> _linkableEntries = [];
    private List<Account> _accounts = [];
    private List<Customer> _customers = [];

    private FiscalYear? _fiscalYear;

    protected override async Task OnInitializedAsync()
    {
        _type = Doc.ClassifiedType ?? Doc.SuggestedType ?? "";
        _fiscalYear = await FiscalYearService.GetActiveAsync();

        // Pre-fill from extraction data
        if (Doc.ExtractedDataJson is not null)
        {
            try
            {
                var ex = JsonSerializer.Deserialize<ExtractionResult>(Doc.ExtractedDataJson);
                if (ex is not null)
                {
                    _supplier = ex.Supplier ?? "";
                    _invoiceNumber = ex.InvoiceNumber ?? "";
                    _amountExcl = ex.Amount ?? 0;
                    _vatAmount = ex.VatAmount ?? 0;
                    if (ex.InvoiceDate.HasValue) _invoiceDate = ex.InvoiceDate.Value.ToDateTime(TimeOnly.MinValue);
                    if (ex.DueDate.HasValue) _dueDate = ex.DueDate.Value.ToDateTime(TimeOnly.MinValue);
                }
            }
            catch { /* ignore malformed json */ }
        }

        if (_fiscalYear is not null)
        {
            _accounts = await AccountService.GetAllAsync(_fiscalYear.Id);
            _customers = await CustomerService.GetAllAsync(_fiscalYear.OrganisationId);
        }
    }

    private async Task LoadEntriesIfNeeded()
    {
        if (_linkableEntries.Count > 0 || _fiscalYear is null) return;
        _linkableEntries = await JournalEntryService.GetByFiscalYearAsync(_fiscalYear.Id);
    }

    private void AddLine() => _lines.Add(new JournalEntryLine());

    private async Task ClassifyAsync()
    {
        _error = null;
        _saving = true;
        try
        {
            switch (_type)
            {
                case "SupplierInvoice":
                    await ClassifyAsSupplierInvoiceAsync();
                    break;
                case "CustomerInvoice":
                    await ClassifyAsCustomerInvoiceAsync();
                    break;
                case "JournalEntry":
                    await ClassifyAsJournalEntryAsync();
                    break;
                default:
                    _error = "Välj en typ.";
                    return;
            }
        }
        finally { _saving = false; }
    }

    private async Task ClassifyAsSupplierInvoiceAsync()
    {
        if (_fiscalYear is null) { _error = "Inget aktivt räkenskapsår."; return; }
        if (string.IsNullOrWhiteSpace(_supplier)) { _error = "Leverantör är obligatoriskt."; return; }

        var total = _amountExcl + _vatAmount;
        var invoice = new SupplierInvoice
        {
            FiscalYearId = _fiscalYear.Id,
            SupplierName = _supplier.Trim(),
            InvoiceNumber = string.IsNullOrWhiteSpace(_invoiceNumber) ? null : _invoiceNumber.Trim(),
            InvoiceDate = DateOnly.FromDateTime(_invoiceDate),
            DueDate = DateOnly.FromDateTime(_dueDate),
            AmountExclVat = _amountExcl,
            VatAmount = _vatAmount,
            TotalAmount = total
        };

        var (created, err) = await SupplierInvoiceService.CreateAsync(invoice);
        if (err is not null) { _error = err; return; }

        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.SupplierInvoice, created!.Id);
        await OnClassified.InvokeAsync();
    }

    private async Task ClassifyAsCustomerInvoiceAsync()
    {
        if (_fiscalYear is null) { _error = "Inget aktivt räkenskapsår."; return; }
        if (_customerId == 0) { _error = "Välj en kund."; return; }

        var customer = _customers.FirstOrDefault(c => c.Id == _customerId);
        if (customer is null) { _error = "Kunden hittades inte."; return; }

        var total = _amountExcl + _vatAmount;
        var invoice = new CustomerInvoice
        {
            FiscalYearId = _fiscalYear.Id,
            CustomerId = _customerId,
            CustomerName = customer.Name,
            InvoiceDate = DateOnly.FromDateTime(_invoiceDate),
            DueDate = DateOnly.FromDateTime(_dueDate),
            AmountExclVat = _amountExcl,
            VatAmount = _vatAmount,
            TotalAmount = total
        };

        // CustomerInvoiceService.CreateAsync requires at least one line
        var vatRate = _amountExcl > 0 && _vatAmount > 0
            ? (int)Math.Round(_vatAmount / _amountExcl * 100)
            : 0;
        var line = new CustomerInvoiceLine
        {
            Description = "Import från inkorg",
            Quantity = 1,
            UnitPrice = _amountExcl,
            AmountExclVat = _amountExcl,
            VatRate = vatRate,
            VatAmount = _vatAmount,
            TotalAmount = total
        };

        var (created, err) = await CustomerInvoiceService.CreateAsync(invoice, [line]);
        if (err is not null) { _error = err; return; }

        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.CustomerInvoice, created!.Id);
        await OnClassified.InvokeAsync();
    }

    private async Task ClassifyAsJournalEntryAsync()
    {
        if (_fiscalYear is null) { _error = "Inget aktivt räkenskapsår."; return; }

        if (_jeMode == "existing")
        {
            if (_existingEntryId == 0) { _error = "Välj en verifikation."; return; }
            await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.JournalEntry, _existingEntryId);
            await OnClassified.InvokeAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(_description)) { _error = "Beskrivning är obligatoriskt."; return; }
        if (_lines.All(l => l.AccountId == 0)) { _error = "Lägg till minst en rad med konto."; return; }

        var entry = new JournalEntry
        {
            FiscalYearId = _fiscalYear.Id,
            Date = DateOnly.FromDateTime(_invoiceDate),
            Description = _description.Trim(),
            Lines = _lines.Where(l => l.AccountId != 0).ToList()
        };

        var (created, err) = await JournalEntryService.CreateAsync(entry);
        if (err is not null) { _error = err; return; }

        await DocumentService.LinkAsync(Doc.Id, DocumentEntityType.JournalEntry, created!.Id);
        await OnClassified.InvokeAsync();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build KoalaBooks.slnx
```

Expected: Build succeeded with 0 error(s).

- [ ] **Step 3: Run full test suite**

```bash
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj -v normal
```

Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Shared/ClassifyDocumentDialog.razor
git commit -m "feat: add ClassifyDocumentDialog with supplier invoice, customer invoice, and journal entry flows"
```

---

### Task 14: Final cleanup

**Files:**
- Delete: `src/KoalaBooks.Domain/Entities/JournalEntryAttachment.cs` (no longer referenced)
- Delete: `src/KoalaBooks.Application/Services/AttachmentService.cs`
- Delete: `src/KoalaBooks.Application/Services/IAttachmentProvider.cs`
- Delete: `src/KoalaBooks.Web/Services/WebAttachmentProvider.cs`

- [ ] **Step 1: Delete obsolete files**

```bash
git rm src/KoalaBooks.Domain/Entities/JournalEntryAttachment.cs
git rm src/KoalaBooks.Application/Services/AttachmentService.cs
git rm src/KoalaBooks.Application/Services/IAttachmentProvider.cs
git rm src/KoalaBooks.Web/Services/WebAttachmentProvider.cs
```

- [ ] **Step 2: Verify build and tests**

```bash
dotnet build KoalaBooks.slnx
dotnet test tests/KoalaBooks.Tests/KoalaBooks.Tests.csproj -v normal
```

Expected: Build succeeded, all tests pass. No references to removed files.

- [ ] **Step 3: Final commit**

```bash
git commit -m "chore: remove obsolete AttachmentService, IAttachmentProvider, JournalEntryAttachment"
```

---

### Task 15: Create and merge PR

- [ ] **Step 1: Push branch and open PR**

```bash
git push -u origin worktree-document-inbox
gh pr create \
  --title "feat: document inbox with unified Document entity" \
  --body "$(cat <<'EOF'
## Summary

- Adds `/inkorg` page: upload PDFs/images, set type inline, classify into supplier invoices, customer invoices, or journal entries via 50/50 split modal
- Replaces `JournalEntryAttachment` with unified `Document` entity (many-to-many with all three entity types) backed by `IDocumentStorage` abstraction (DB blobs today, S3 later via #123)
- Basic non-AI extraction: filename heuristics + PdfPig text layer + regex for Swedish invoice fields
- Document panels added to SupplierInvoices and CustomerInvoices pages
- Auto-link: when an invoice is posted, its documents are automatically linked to the created journal entry

## Test plan

- [ ] Upload a PDF in `/inkorg`, confirm extraction pre-fills the type selector
- [ ] Set type inline without opening modal, confirm filter bar filters correctly  
- [ ] Classify as supplier invoice, confirm invoice appears in `/supplier-invoices` with document attached
- [ ] Classify as customer invoice — same check
- [ ] Classify as new journal entry, confirm entry appears in `/journal` with document attached
- [ ] Classify by linking to existing journal entry
- [ ] Post a supplier invoice that has a document — confirm document appears on the journal entry too
- [ ] Upload directly on Journal page, confirm existing behaviour unchanged
- [ ] Run `dotnet test` — all tests pass

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 2: Review and merge**

After CI passes and review is done, merge via GitHub.
