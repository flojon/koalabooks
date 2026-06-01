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
    public async Task UploadAsync_SetsSuggestedTypeFromFilename_ClassifiedTypeRemainsNull()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", []);

        Assert.Equal("SupplierInvoice", doc!.SuggestedType);
        Assert.Null(doc.ClassifiedType);
    }

    [Fact]
    public async Task UploadAsync_RejectsDisallowedContentType()
    {
        var svc = _fx.MakeDocumentService();
        var (doc, err) = await svc.UploadAsync("bad.html", "text/html", [1, 2, 3]);

        Assert.Null(doc);
        Assert.NotNull(err);
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
        var pending = await svc.GetPendingAsync();
        var updated = pending.First(d => d.Id == doc.Id);
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
