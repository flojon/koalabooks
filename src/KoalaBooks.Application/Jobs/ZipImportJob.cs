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

// The ResultJson payload for a BackgroundJobRun with JobType == ZipImport. Written after
// every processed entry (not just at CompleteAsync) so a Hangfire retry that resumes
// mid-zip can recover the tally from entries an earlier attempt already accounted for —
// Run.ProcessedCount alone tells RunAsync *where* to resume, but not what the running
// import/skip counts were. Read directly by Inbox.razor via plain JSON property-name
// matching — no shared type reference between Components and Application.Jobs needed.
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

    // A run left un-Completed after all 3 retries are exhausted is caught by
    // BackgroundJobRunFailureFilter (PR #285), which marks it Failed so
    // BackgroundJobStatusPoller/Inbox.razor can surface that instead of polling forever.
    //
    // fileName/stagingOid arrive as ordinary Hangfire job arguments (serialized once at
    // Enqueue time, replayed unchanged on every automatic retry) rather than being looked
    // up from a persisted row — BackgroundJobRun has no room for job-specific input
    // columns by design (see the design doc's Architecture §1).
    //
    // context is Hangfire's special PerformContext parameter: real callers (see
    // HangfireZipImportQueue) pass null at enqueue time and Hangfire substitutes the real
    // context at execution time, giving BackgroundJob.Id — the same id across every
    // automatic retry of this job, which is what LoadRunAsync's double-claim protection
    // keys on. Unit tests call RunAsync directly with no context, falling back to a fresh
    // random id; no test depends on jobId being stable across two separate RunAsync calls
    // (retry-resume tests simulate the earlier attempt by writing directly to the row).
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int runId, string fileName, uint stagingOid, PerformContext? context = null)
    {
        var jobId = context?.BackgroundJob.Id ?? Guid.NewGuid().ToString();
        if (!await LoadRunAsync(runId, jobId).ConfigureAwait(false)) return;

        var documentService = new DocumentService(
            Db, storage, extractionQueue, zipImportQueue, new BackgroundJobRunService(Db, dbOptions, Tenant), Tenant);

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
