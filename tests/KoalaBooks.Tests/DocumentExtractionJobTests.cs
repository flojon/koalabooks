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
    public async Task RunAsync_UserAlreadyClassifiedWithDate_DoesNotOverwriteDocumentDate()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

        // User classifies the document (via the "Bokför" dialog) while extraction is still Pending.
        var userChosenDate = new DateOnly(2026, 1, 10);
        await svc.UpdateMetadataAsync(doc!.Id, "SupplierInvoice", userChosenDate);

        // The already-enqueued job completes afterwards with a different extracted date.
        var extractor = new StubExtractor(new ExtractionResult(
            "SupplierInvoice", "ACME AB", 1000m, 250m, new DateOnly(2026, 3, 15), null, "INV-001"));
        var job = new DocumentExtractionJob(_fx.Db, storage, extractor, NullLogger<DocumentExtractionJob>.Instance);

        await job.RunAsync(doc.Id);

        var updated = await _fx.Db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == doc.Id);
        Assert.Equal(userChosenDate, updated.DocumentDate);
        Assert.Equal(ExtractionStatus.Completed, updated.ExtractionStatus);
    }

    [Fact]
    public async Task RunAsync_ExtractorThrows_MarksFailed_DoesNotThrow()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", new MemoryStream([1, 2, 3]));

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
