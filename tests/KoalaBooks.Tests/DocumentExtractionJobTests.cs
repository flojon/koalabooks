using KoalaBooks.Application.Jobs;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
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
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream([1, 2, 3]));

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
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream([1, 2, 3]));

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
    public async Task RunAsync_ClassifiedConcurrentlyDuringExtraction_DoesNotOverwriteDocumentDate()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream([1, 2, 3]));

        var userChosenDate = new DateOnly(2026, 1, 10);
        var extractedDate = new DateOnly(2026, 3, 15);

        // Simulates the user classifying via "Bokför" on a separate DB connection while
        // this job's extractor.ExtractAsync call is still in flight — the window the
        // sequential-only "already classified before RunAsync starts" test above can't
        // reach. The job's own `doc` is already loaded by this point, so only the
        // concurrency-token retry in SaveChangesResolvingConcurrencyAsync can catch this.
        var extractor = new ConcurrentClassifyExtractor(
            _fx.Db.Database.GetConnectionString()!, _fx.OrganisationId, doc!.Id, userChosenDate,
            new ExtractionResult("SupplierInvoice", "ACME AB", 1000m, 250m, extractedDate, null, "INV-001"));
        var job = new DocumentExtractionJob(_fx.Db, storage, extractor, NullLogger<DocumentExtractionJob>.Instance);

        await job.RunAsync(doc.Id);

        var updated = await _fx.Db.Documents.IgnoreQueryFilters().FirstAsync(d => d.Id == doc.Id);
        Assert.Equal(userChosenDate, updated.DocumentDate);
        Assert.Equal("SupplierInvoice", updated.SuggestedType);
        Assert.Equal(ExtractionStatus.Completed, updated.ExtractionStatus);
    }

    [Fact]
    public async Task RunAsync_DocumentDeletedDuringExtraction_NoOpsWithoutThrowing()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("faktura.pdf", "application/pdf", () => new MemoryStream([1, 2, 3]));

        var extractor = new ConcurrentDeleteExtractor(
            _fx.Db.Database.GetConnectionString()!, _fx.OrganisationId, doc!.Id,
            new ExtractionResult("SupplierInvoice", "ACME AB", 1000m, 250m, new DateOnly(2026, 3, 15), null, "INV-001"));
        var job = new DocumentExtractionJob(_fx.Db, storage, extractor, NullLogger<DocumentExtractionJob>.Instance);

        await job.RunAsync(doc.Id);

        var stillThere = await _fx.Db.Documents.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == doc.Id);
        Assert.Null(stillThere);
    }

    [Fact]
    public async Task RunAsync_ExtractorThrows_MarksFailed_DoesNotThrow()
    {
        var storage = new DbDocumentStorage(_fx.Db);
        var svc = _fx.MakeDocumentService(storage);
        var (doc, _) = await svc.UploadAsync("unknown.pdf", "application/pdf", () => new MemoryStream([1, 2, 3]));

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
        var (doc, _) = await svc.UploadAsync("leverantörsfaktura.pdf", "application/pdf", () => new MemoryStream());

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

// Performs a write through a second, independent AppDbContext/connection while the job's
// extractor.ExtractAsync call is in flight — this is what a concurrent Blazor circuit
// (a different request, different DbContext) actually looks like, unlike calling
// UpdateMetadataAsync on the job's own _fx.Db before RunAsync starts.
file class ConcurrentClassifyExtractor(
    string connectionString, int organisationId, int documentId, DateOnly userChosenDate, ExtractionResult result)
    : IDocumentExtractor
{
    public async Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        using var concurrentDb = new AppDbContext(options, TestFixture.MakeTenant(organisationId));
        var concurrentSvc = new DocumentService(
            concurrentDb, new DbDocumentStorage(concurrentDb), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), TestFixture.MakeTenant(organisationId));
        await concurrentSvc.UpdateMetadataAsync(documentId, "SupplierInvoice", userChosenDate);
        return result;
    }
}

file class ConcurrentDeleteExtractor(string connectionString, int organisationId, int documentId, ExtractionResult result)
    : IDocumentExtractor
{
    public async Task<ExtractionResult> ExtractAsync(string fileName, string contentType, byte[] data)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        using var concurrentDb = new AppDbContext(options, TestFixture.MakeTenant(organisationId));
        var concurrentSvc = new DocumentService(
            concurrentDb, new DbDocumentStorage(concurrentDb), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), TestFixture.MakeTenant(organisationId));
        await concurrentSvc.DeleteAsync(documentId);
        return result;
    }
}
