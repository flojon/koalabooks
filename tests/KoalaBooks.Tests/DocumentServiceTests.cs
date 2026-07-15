using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class DocumentServiceTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task UploadAsync_StoresDocumentAndReturnsIt()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream(new byte[] { 1, 2, 3 }));

        Assert.Null(err);
        Assert.NotNull(doc);
        Assert.Equal("faktura.pdf", doc.FileName);
        Assert.Equal(3, doc.FileSize);
        Assert.NotEmpty(doc.StorageKey);
    }

    [Fact]
    public async Task UploadAsync_SetsExtractionStatusPending_NoSuggestionYet()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", new MemoryStream());

        Assert.Equal(ExtractionStatus.Pending, doc!.ExtractionStatus);
        Assert.Null(doc.SuggestedType);
        Assert.Null(doc.ClassifiedType);
    }

    [Fact]
    public async Task UploadAsync_RejectsDisallowedContentType()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("bad.html", "text/html", new MemoryStream([1, 2, 3]));

        Assert.Null(doc);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task UploadAsync_RejectsOversizedFile()
    {
        var svc = _fx.MakeDocumentService();
        var bigData = new byte[11 * 1024 * 1024];
        var (doc, err) = await svc.UploadAsync("big.pdf", "application/pdf", new MemoryStream(bigData));

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

        await svc.UploadAsync("unlinked.pdf", "application/pdf", new MemoryStream([1]));
        var (linked, _) = await svc.UploadAsync("linked.pdf", "application/pdf", new MemoryStream([2]));
        await svc.LinkAsync(linked!.Id, DocumentEntityType.JournalEntry, entry.Id);

        var pending = await svc.GetPendingAsync();

        Assert.Single(pending);
        Assert.Equal("unlinked.pdf", pending[0].FileName);
    }

    [Fact]
    public async Task UpdateMetadataAsync_SetsTypeAndDate()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", new MemoryStream());
        var date = new DateOnly(2026, 3, 15);

        var err = await svc.UpdateMetadataAsync(doc!.Id, "CustomerInvoice", date);

        Assert.Null(err);
        var pending = await svc.GetPendingAsync();
        var updated = pending.First(d => d.Id == doc.Id);
        Assert.Equal("CustomerInvoice", updated.ClassifiedType);
        Assert.Equal(date, updated.DocumentDate);
    }

    [Fact]
    public async Task UpdateMetadataAsync_StaleTrackedEntityFromUpload_RetriesInsteadOfThrowing()
    {
        // svc's doc stays tracked with a stale xmin after upload; a second DbContext simulates
        // the background extraction job writing to the row concurrently.
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_fx.Db.Database.GetConnectionString()!).Options;
        await using (var concurrentDb = new AppDbContext(options, TestFixture.MakeTenant(_fx.OrganisationId)))
        {
            var concurrentDoc = await concurrentDb.Documents.FirstAsync(d => d.Id == doc!.Id);
            concurrentDoc.SuggestedType = "SupplierInvoice";
            concurrentDoc.ExtractionStatus = ExtractionStatus.Completed;
            await concurrentDb.SaveChangesAsync();
        }

        var date = new DateOnly(2026, 3, 15);
        var err = await svc.UpdateMetadataAsync(doc!.Id, "CustomerInvoice", date);

        Assert.Null(err);

        // Verify through a fresh DbContext — _fx.Db still has the stale tracked instance.
        await using var verifyDb = new AppDbContext(options, TestFixture.MakeTenant(_fx.OrganisationId));
        var updated = await verifyDb.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == doc.Id);
        Assert.Equal("CustomerInvoice", updated.ClassifiedType);
        Assert.Equal(date, updated.DocumentDate);
        Assert.Equal("SupplierInvoice", updated.SuggestedType); // concurrent write preserved, not clobbered
    }

    [Fact]
    public async Task UploadAsync_EnqueuesExtractionJob()
    {
        var queue = new RecordingExtractionQueue();
        var svc = _fx.MakeDocumentService(queue);

        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

        Assert.Equal(doc!.Id, Assert.Single(queue.EnqueuedDocumentIds));
    }

    [Fact]
    public async Task GetPendingAsync_SortsByDocumentDate()
    {
        var svc = _fx.MakeDocumentService();
        var (d1, _) = await svc.UploadAsync("a.pdf", "application/pdf", new MemoryStream([1]));
        var (d2, _) = await svc.UploadAsync("b.pdf", "application/pdf", new MemoryStream([2]));

        await svc.UpdateMetadataAsync(d1!.Id, null, new DateOnly(2026, 1, 1));
        await svc.UpdateMetadataAsync(d2!.Id, null, new DateOnly(2026, 6, 1));

        var ascResult = await svc.GetPendingAsync(sortBy: "documentDate", sortAsc: true);
        Assert.Equal(d1.Id, ascResult[0].Id);
        Assert.Equal(d2.Id, ascResult[1].Id);

        var descResult = await svc.GetPendingAsync(sortBy: "documentDate", sortAsc: false);
        Assert.Equal(d2.Id, descResult[0].Id);
        Assert.Equal(d1.Id, descResult[1].Id);
    }

    [Fact]
    public async Task GetLinkedAsync_ReturnsDocumentsForJournalEntry()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var entry = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);

        var (doc, _) = await svc.UploadAsync("receipt.pdf", "application/pdf", new MemoryStream([5]));
        await svc.LinkAsync(doc!.Id, DocumentEntityType.JournalEntry, entry.Id);

        var linked = await svc.GetLinkedAsync(DocumentEntityType.JournalEntry, entry.Id);

        Assert.Single(linked);
        Assert.Equal("receipt.pdf", linked[0].FileName);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDocumentAndData()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("todelete.pdf", "application/pdf", new MemoryStream([9, 8, 7]));

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
        var (doc, _) = await svc.UploadAsync("file.pdf", "application/pdf", new MemoryStream([10, 20, 30]));

        var result = await svc.GetDownloadAsync(doc!.Id);

        Assert.NotNull(result);
        Assert.Equal("application/pdf", result.Value.ContentType);
        Assert.Equal(new byte[] { 10, 20, 30 }, result.Value.Data);
    }

    [Fact]
    public async Task UploadAsync_RollsBackDocumentRowWhenStorageFails()
    {
        var svc = _fx.MakeDocumentService(new FailingStorage());
        var (doc, err) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

        Assert.Null(doc);
        Assert.NotNull(err);

        // The metadata row must not survive the storage failure
        var pending = await _fx.MakeDocumentService().GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task UploadAsync_AcceptsImageJpgMimeType()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("photo.jpg", "image/jpg", new MemoryStream([1, 2, 3]));

        Assert.Null(err);
        Assert.NotNull(doc);
    }

    [Fact]
    public async Task PostSupplierInvoice_AutoLinksDocumentToJournalEntry()
    {
        var docSvc = _fx.MakeDocumentService();
        var supplierSvc = new SupplierInvoiceService(_fx.Db);
        var fy = _fx.CreateFiscalYear();
        var (expense, payable, _, _, _) = _fx.CreateStandardAccounts(fy.Id);

        var invoice = new SupplierInvoice
        {
            FiscalYearId = fy.Id,
            SupplierName = "ACME AB",
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            DueDate = DateOnly.FromDateTime(DateTime.Today.AddDays(30)),
            AmountExclVat = 800m,
            VatAmount = 200m,
            TotalAmount = 1000m
        };
        var (created, _) = await supplierSvc.CreateAsync(invoice);

        var (doc, _) = await docSvc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1]));
        await docSvc.LinkAsync(doc!.Id, DocumentEntityType.SupplierInvoice, created!.Id);

        var (posted, err) = await supplierSvc.PostAsync(created.Id, expense.Id, payable.Id, null);
        Assert.Null(err);

        var linked = await docSvc.GetLinkedAsync(DocumentEntityType.JournalEntry, posted!.JournalEntryId!.Value);
        Assert.Single(linked);
        Assert.Equal("faktura.pdf", linked[0].FileName);
    }

    [Fact]
    public async Task GetCountsForJournalEntriesAsync_CountsCorrectly()
    {
        var svc = _fx.MakeDocumentService();
        var fy = _fx.CreateFiscalYear();
        var (debit, credit, _, _, _) = _fx.CreateStandardAccounts(fy.Id);
        var e1 = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 100m);
        var e2 = await _fx.CreateAndPostEntryAsync(fy.Id, debit.Id, credit.Id, 200m);

        var (doc1, _) = await svc.UploadAsync("a.pdf", "application/pdf", new MemoryStream([1]));
        var (doc2, _) = await svc.UploadAsync("b.pdf", "application/pdf", new MemoryStream([2]));
        var (doc3, _) = await svc.UploadAsync("c.pdf", "application/pdf", new MemoryStream([3]));

        await svc.LinkAsync(doc1!.Id, DocumentEntityType.JournalEntry, e1.Id);
        await svc.LinkAsync(doc2!.Id, DocumentEntityType.JournalEntry, e1.Id);
        await svc.LinkAsync(doc3!.Id, DocumentEntityType.JournalEntry, e2.Id);

        var counts = await svc.GetCountsForJournalEntriesAsync([e1.Id, e2.Id]);

        Assert.Equal(2, counts[e1.Id]);
        Assert.Equal(1, counts[e2.Id]);
    }

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

    [Fact]
    public async Task UploadZipAsync_SkipsCorruptEntry_RestOfBatchStillImports()
    {
        var svc = _fx.MakeDocumentService();
        var zip = CorruptEntryData(BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.pdf", new byte[500])), "bad.pdf");

        var (result, _) = await svc.UploadZipAsync(zip);

        Assert.Single(result!.Imported);
        Assert.Equal("good.pdf", result.Imported[0].FileName);
        Assert.Single(result.Skipped);
        Assert.Equal("bad.pdf", result.Skipped[0].FileName);
    }

    private static byte[] CorruptEntryData(byte[] zipBytes, string entryName)
    {
        var corrupted = (byte[])zipBytes.Clone();
        for (var i = 0; i < corrupted.Length - 4; i++)
        {
            if (corrupted[i] == 0x50 && corrupted[i + 1] == 0x4B && corrupted[i + 2] == 0x03 && corrupted[i + 3] == 0x04)
            {
                var nameLen = BitConverter.ToUInt16(corrupted, i + 26);
                var extraLen = BitConverter.ToUInt16(corrupted, i + 28);
                var nameStart = i + 30;
                var name = System.Text.Encoding.UTF8.GetString(corrupted, nameStart, nameLen);
                if (name == entryName)
                {
                    var compressedSize = BitConverter.ToInt32(corrupted, i + 18);
                    var dataStart = nameStart + nameLen + extraLen;
                    for (var j = dataStart; j < dataStart + compressedSize; j++)
                        corrupted[j] = (byte)~corrupted[j];
                    return corrupted;
                }
            }
        }
        throw new InvalidOperationException($"entry {entryName} not found in zip for corruption");
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
}

file class FailingStorage : IDocumentStorage
{
    public Task<string> SaveAsync(int documentId, string contentType, Stream data) =>
        throw new InvalidOperationException("simulated storage failure");

    public Task<byte[]> LoadAsync(string storageKey) => Task.FromResult(Array.Empty<byte>());
    public Task DeleteAsync(string storageKey) => Task.CompletedTask;
}

file class RecordingExtractionQueue : IDocumentExtractionQueue
{
    public List<int> EnqueuedDocumentIds { get; } = [];
    public void Enqueue(int documentId) => EnqueuedDocumentIds.Add(documentId);
}
