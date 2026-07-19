using Hangfire;
using Hangfire.Server;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.IO.Compression;
using System.Text.Json;

namespace KoalaBooks.Application.Jobs;

public record SkippedEntry(string FileName, string Reason);

// BackgroundJobRun.ResultJson payload for a ZipImport run. Written after every entry
// (not just at CompleteAsync) so a resumed retry can recover the running tally, not just
// ProcessedCount. Read by Inbox.razor via plain JSON property matching, no shared type ref.
public record ZipImportResult(string FileName, int ImportedCount, int SkippedCount, List<SkippedEntry> SkippedReasons);

public class ZipImportJob(
    DbContextOptions<AppDbContext> dbOptions,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    IZipImportQueue zipImportQueue,
    ILogger<ZipImportJob> logger) : BackgroundJobRunBase(dbOptions)
{
    private static readonly Dictionary<string, string> ZipEntryContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    // fileName/stagingOid are plain Hangfire job arguments, replayed unchanged on every
    // retry, rather than columns on BackgroundJobRun. context is Hangfire's PerformContext:
    // real callers pass null at enqueue time and Hangfire substitutes the real one at
    // execution, giving a stable BackgroundJob.Id that LoadRunAsync's double-claim check
    // keys on; tests calling RunAsync directly fall back to a fresh random id.
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int runId, string fileName, uint stagingOid, PerformContext? context = null)
    {
        var jobId = context?.BackgroundJob.Id ?? Guid.NewGuid().ToString();
        if (!await LoadRunAsync(runId, jobId).ConfigureAwait(false)) return;

        var documentService = new DocumentService(
            Db, storage, extractionQueue, zipImportQueue, new BackgroundJobRunService(Db, DbOptions, Tenant), Tenant);

        var (importedCount, skippedCount, skipped) = Run.ResultJson is null
            ? (0, 0, new List<SkippedEntry>())
            : ToTuple(JsonSerializer.Deserialize<ZipImportResult>(Run.ResultJson)!);

        var tempPath = Path.GetTempFileName();
        try
        {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
#pragma warning restore CA2007
            {
                var readStrategy = Db.Database.CreateExecutionStrategy();
                await readStrategy.ExecuteAsync(async () =>
                {
                    // A retry re-invokes this whole delegate: reset the temp file so a
                    // prior attempt's partial write (from a transient failure mid-copy)
                    // can't leave leftover bytes ahead of this attempt's data.
                    tempStream.Position = 0;
                    tempStream.SetLength(0);
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                    await using var tx = await Db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                    var conn = (NpgsqlConnection)Db.Database.GetDbConnection();
                    await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, stagingOid, tempStream).ConfigureAwait(false);
                    await tx.CommitAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
                tempStream.Position = 0;

                ZipArchive? archive = null;
                try
                {
                    try
                    {
                        archive = new ZipArchive(tempStream, ZipArchiveMode.Read, leaveOpen: true);
                    }
                    catch (InvalidDataException)
                    {
                        skipped.Add(new SkippedEntry("(zip-fil)", "Ogiltig zip-fil."));
                        skippedCount++;
                        await CompleteAsync(BackgroundJobStatus.Completed,
                            new ZipImportResult(fileName, importedCount, skippedCount, skipped)).ConfigureAwait(false);
                        await DeleteStagingAsync(stagingOid, runId).ConfigureAwait(false);
                        return;
                    }

                    var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

                    foreach (var entry in fileEntries.Skip(Run.ProcessedCount))
                    {
                        if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
                        {
                            skipped.Add(new SkippedEntry(entry.Name, "Otillåten filtyp."));
                            skippedCount++;
                        }
                        else
                        {
                            try
                            {
                                var entryFullName = entry.FullName;
                                var (doc, err) = await documentService.UploadAsync(
                                    entry.Name, contentType, () => archive.GetEntry(entryFullName)!.Open()).ConfigureAwait(false);
                                if (doc is not null)
                                {
                                    importedCount++;
                                }
                                else
                                {
                                    skipped.Add(new SkippedEntry(entry.Name, err ?? "Okänt fel."));
                                    skippedCount++;
                                }
                            }
                            catch (InvalidDataException)
                            {
                                skipped.Add(new SkippedEntry(entry.Name, "Skadad fil."));
                                skippedCount++;
                            }
                        }

                        // Written before SaveProgressAsync's own SaveChangesAsync so both
                        // land in the same round trip — see the ZipImportResult doc
                        // comment on why this interim write matters for retry-resume.
                        Run.ResultJson = JsonSerializer.Serialize(new ZipImportResult(fileName, importedCount, skippedCount, skipped));
                        await SaveProgressAsync(Run.ProcessedCount + 1).ConfigureAwait(false);
                    }
                }
                finally
                {
                    archive?.Dispose();
                }
            }

            await CompleteAsync(BackgroundJobStatus.Completed,
                new ZipImportResult(fileName, importedCount, skippedCount, skipped)).ConfigureAwait(false);
            await DeleteStagingAsync(stagingOid, runId).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static (int, int, List<SkippedEntry>) ToTuple(ZipImportResult r) => (r.ImportedCount, r.SkippedCount, r.SkippedReasons);

    private async Task DeleteStagingAsync(uint stagingOid, int runId)
    {
        try
        {
            var strategy = Db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await Db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var conn = (NpgsqlConnection)Db.Database.GetDbConnection();
                await PostgresLargeObjects.DeleteLargeObjectAsync(conn, stagingOid).ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The run has already been marked Completed and the local temp file cleaned
            // up — a leaked LO here is a minor storage-cleanup miss, not a correctness
            // issue for the run itself, so log and move on rather than failing the job.
            logger.LogWarning(ex, "Failed to delete staging large object {Oid} for run {RunId}", stagingOid, runId);
        }
    }
}
