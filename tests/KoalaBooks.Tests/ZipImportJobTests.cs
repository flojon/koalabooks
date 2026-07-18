using System.IO.Compression;
using System.Text.Json;
using KoalaBooks.Application.Jobs;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace KoalaBooks.Tests;

public class ZipImportJobTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<int> StageZipAsync(byte[] zipBytes, int entryCount)
    {
        uint oid;
        await using (var tx = await _fx.Db.Database.BeginTransactionAsync())
        {
            var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
            (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(zipBytes));
            await tx.CommitAsync();
        }

        var batch = new ZipImportBatch
        {
            OrganisationId = _fx.OrganisationId,
            StagingOid = oid,
            TotalEntries = entryCount,
        };
        _fx.Db.ZipImportBatches.Add(batch);
        await _fx.Db.SaveChangesAsync();
        return batch.Id;
    }

    private ZipImportJob MakeJob() =>
        new ZipImportJob(_fx.Options, new DbDocumentStorage(_fx.Db), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), NullLogger<ZipImportJob>.Instance);

    [Fact]
    public async Task RunAsync_ImportsAllValidEntries()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        // AsNoTracking: RunAsync updates the batch through its own AppDbContext, not
        // _fx.Db — without this, _fx.Db's change tracker returns the stale pre-run
        // instance it's held onto since StageZipAsync's Add, not the persisted row.
        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batchId);
        Assert.True(batch.Done);
        Assert.Equal(2, batch.ProcessedEntries);
        Assert.Equal(2, batch.ImportedCount);
        Assert.Equal(0, batch.SkippedCount);
        Assert.Null(batch.StagingOid);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.Contains(docs, d => d.FileName == "a.pdf");
        Assert.Contains(docs, d => d.FileName == "b.png");
    }

    [Fact]
    public async Task RunAsync_FlattensNestedFolderPaths()
    {
        var zip = BuildZip(("invoices/2026/faktura.pdf", new byte[] { 1, 2, 3 }));
        var batchId = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(batchId);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsDirectoryEntries()
    {
        var zip = BuildZipWithDirectoryEntry();
        var batchId = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(batchId);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsInvalidEntryType_ReportsReason()
    {
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.exe", new byte[] { 1, 2, 3 }));
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batchId);
        Assert.Equal(1, batch.ImportedCount);
        Assert.Equal(1, batch.SkippedCount);
        var skipped = JsonSerializer.Deserialize<List<SkippedEntry>>(batch.SkippedReasonsJson)!;
        Assert.Single(skipped);
        Assert.Equal("bad.exe", skipped[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsOversizedEntry()
    {
        var bigData = new byte[11 * 1024 * 1024];
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("big.pdf", bigData));
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batchId);
        Assert.Equal(1, batch.ImportedCount);
        Assert.Equal(1, batch.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_CorruptZipContainer_MarksDoneImmediately_NoEntriesProcessed()
    {
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };
        var batchId = await StageZipAsync(corruptBytes, 0);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batchId);
        Assert.True(batch.Done);
        Assert.Equal(0, batch.ProcessedEntries);
        var skipped = JsonSerializer.Deserialize<List<SkippedEntry>>(batch.SkippedReasonsJson)!;
        Assert.Single(skipped);
    }

    [Fact]
    public async Task RunAsync_SkipsCorruptEntry_RestOfBatchStillImports()
    {
        var zip = CorruptEntryData(BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.pdf", new byte[500])), "bad.pdf");
        var batchId = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(batchId);

        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batchId);
        Assert.Equal(1, batch.ImportedCount);
        Assert.Equal(1, batch.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_ResumesFromProcessedEntries_DoesNotReimportOnRetry()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }), ("b.pdf", new byte[] { 2 }), ("c.pdf", new byte[] { 3 }));
        var batchId = await StageZipAsync(zip, 3);

        // Simulate a first attempt that processed the first entry then crashed
        // (e.g. a transient storage failure) before saving further progress.
        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        var svc = _fx.MakeDocumentService();
        await svc.UploadAsync("a.pdf", "application/pdf", () => new MemoryStream(new byte[] { 1 }));
        batch.ProcessedEntries = 1;
        batch.ImportedCount = 1;
        await _fx.Db.SaveChangesAsync();

        // Retry: RunAsync should resume from entry index 1, not reprocess "a.pdf".
        await MakeJob().RunAsync(batchId);

        var finalBatch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().AsNoTracking().FirstAsync(b => b.Id == batchId);
        Assert.True(finalBatch.Done);
        Assert.Equal(3, finalBatch.ProcessedEntries);
        Assert.Equal(3, finalBatch.ImportedCount);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs, d => d.FileName == "a.pdf"); // exactly one, not duplicated
        Assert.Single(docs, d => d.FileName == "b.pdf");
        Assert.Single(docs, d => d.FileName == "c.pdf");
    }

    [Fact]
    public async Task RunAsync_UnknownBatchId_NoOpsWithoutThrowing()
    {
        await MakeJob().RunAsync(999_999);
    }

    [Fact]
    public async Task RunAsync_AlreadyDoneBatch_NoOpsWithoutThrowing()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }));
        var batchId = await StageZipAsync(zip, 1);
        var batch = await _fx.Db.ZipImportBatches.IgnoreQueryFilters().FirstAsync(b => b.Id == batchId);
        batch.Done = true;
        await _fx.Db.SaveChangesAsync();

        await MakeJob().RunAsync(batchId); // must not throw even though StagingOid still points at a valid LO

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Empty(docs); // confirms it didn't reprocess
    }

    private static byte[] BuildZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
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
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("empty_folder/");
            var entry = archive.CreateEntry("faktura.pdf");
            using var entryStream = entry.Open();
            var data = new byte[] { 1, 2, 3 };
            entryStream.Write(data, 0, data.Length);
        }
        return ms.ToArray();
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
}
