using System.IO.Compression;
using System.Text.Json;
using KoalaBooks.Application.Jobs;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace KoalaBooks.Tests;

public class ZipImportJobTests : IDisposable
{
    private readonly TestFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private async Task<(int RunId, uint StagingOid)> StageZipAsync(byte[] zipBytes, int entryCount)
    {
        uint oid;
        await using (var tx = await _fx.Db.Database.BeginTransactionAsync())
        {
            var conn = (NpgsqlConnection)_fx.Db.Database.GetDbConnection();
            (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, new MemoryStream(zipBytes));
            await tx.CommitAsync();
        }

        var run = await _fx.MakeBackgroundJobRunService().CreateRunAsync(BackgroundJobType.ZipImport, entryCount);
        return (run.Id, oid);
    }

    private ZipImportJob MakeJob() =>
        new ZipImportJob(_fx.Options, new DbDocumentStorage(_fx.Db), new NoOpDocumentExtractionQueue(),
            new NoOpZipImportQueue(), NullLogger<ZipImportJob>.Instance);

    private async Task<BackgroundJobRun> ReloadRunAsync(int runId) =>
        await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == runId);

    private static ZipImportResult ParseResult(BackgroundJobRun run) =>
        JsonSerializer.Deserialize<ZipImportResult>(run.ResultJson!)!;

    [Fact]
    public async Task RunAsync_ImportsAllValidEntries()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1, 2, 3 }), ("b.png", new byte[] { 4, 5 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var run = await ReloadRunAsync(runId);
        Assert.Equal(BackgroundJobStatus.Completed, run.Status);
        Assert.Equal(2, run.ProcessedCount);
        var result = ParseResult(run);
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Equal(2, docs.Count);
        Assert.Contains(docs, d => d.FileName == "a.pdf");
        Assert.Contains(docs, d => d.FileName == "b.png");
    }

    [Fact]
    public async Task RunAsync_FlattensNestedFolderPaths()
    {
        var zip = BuildZip(("invoices/2026/faktura.pdf", new byte[] { 1, 2, 3 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsDirectoryEntries()
    {
        var zip = BuildZipWithDirectoryEntry();
        var (runId, stagingOid) = await StageZipAsync(zip, 1);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs);
        Assert.Equal("faktura.pdf", docs[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsInvalidEntryType_ReportsReason()
    {
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.exe", new byte[] { 1, 2, 3 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var result = ParseResult(await ReloadRunAsync(runId));
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(result.SkippedReasons);
        Assert.Equal("bad.exe", result.SkippedReasons[0].FileName);
    }

    [Fact]
    public async Task RunAsync_SkipsOversizedEntry()
    {
        var bigData = new byte[11 * 1024 * 1024];
        var zip = BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("big.pdf", bigData));
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var result = ParseResult(await ReloadRunAsync(runId));
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_CorruptZipContainer_CompletesImmediately_NoEntriesProcessed()
    {
        var corruptBytes = new byte[] { 1, 2, 3, 4, 5 };
        var (runId, stagingOid) = await StageZipAsync(corruptBytes, 0);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var run = await ReloadRunAsync(runId);
        Assert.Equal(BackgroundJobStatus.Completed, run.Status);
        Assert.Equal(0, run.ProcessedCount);
        var result = ParseResult(run);
        Assert.Single(result.SkippedReasons);
    }

    [Fact]
    public async Task RunAsync_SkipsCorruptEntry_RestOfBatchStillImports()
    {
        var zip = CorruptEntryData(BuildZip(("good.pdf", new byte[] { 1, 2, 3 }), ("bad.pdf", new byte[500])), "bad.pdf");
        var (runId, stagingOid) = await StageZipAsync(zip, 2);

        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var result = ParseResult(await ReloadRunAsync(runId));
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public async Task RunAsync_ResumesFromProcessedEntries_DoesNotReimportOnRetry()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }), ("b.pdf", new byte[] { 2 }), ("c.pdf", new byte[] { 3 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 3);

        // Simulate a first attempt that processed the first entry then crashed (e.g. a
        // transient storage failure) before the job's process could move past it — the
        // run is left Pending (LoadRunAsync never got the chance to flip it to Running
        // and record a ClaimedByJobId), with ProcessedCount/ResultJson already reflecting
        // that one entry, exactly what SaveProgressAsync/Run.ResultJson would have
        // persisted mid-loop.
        var svc = _fx.MakeDocumentService();
        await svc.UploadAsync("a.pdf", "application/pdf", () => new MemoryStream(new byte[] { 1 }));
        var run = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == runId);
        run.ProcessedCount = 1;
        run.ResultJson = JsonSerializer.Serialize(new ZipImportResult("test.zip", 1, 0, []));
        await _fx.Db.SaveChangesAsync();

        // Retry: RunAsync should resume from entry index 1, not reprocess "a.pdf", and the
        // final ImportedCount must include the entry the simulated first attempt already
        // imported.
        await MakeJob().RunAsync(runId, "test.zip", stagingOid);

        var finalRun = await ReloadRunAsync(runId);
        Assert.Equal(BackgroundJobStatus.Completed, finalRun.Status);
        Assert.Equal(3, finalRun.ProcessedCount);
        var result = ParseResult(finalRun);
        Assert.Equal(3, result.ImportedCount);

        var docs = await _fx.Db.Documents.IgnoreQueryFilters().Where(d => d.OrganisationId == _fx.OrganisationId).ToListAsync();
        Assert.Single(docs, d => d.FileName == "a.pdf"); // exactly one, not duplicated
        Assert.Single(docs, d => d.FileName == "b.pdf");
        Assert.Single(docs, d => d.FileName == "c.pdf");
    }

    [Fact]
    public async Task RunAsync_UnknownRunId_NoOpsWithoutThrowing()
    {
        await MakeJob().RunAsync(999_999, "test.zip", 0);
    }

    [Fact]
    public async Task RunAsync_AlreadyCompletedRun_NoOpsWithoutThrowing()
    {
        var zip = BuildZip(("a.pdf", new byte[] { 1 }));
        var (runId, stagingOid) = await StageZipAsync(zip, 1);
        var run = await _fx.Db.BackgroundJobRuns.IgnoreQueryFilters().FirstAsync(r => r.Id == runId);
        run.Status = BackgroundJobStatus.Completed;
        await _fx.Db.SaveChangesAsync();

        await MakeJob().RunAsync(runId, "test.zip", stagingOid); // must not throw even though stagingOid still points at a valid LO

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
