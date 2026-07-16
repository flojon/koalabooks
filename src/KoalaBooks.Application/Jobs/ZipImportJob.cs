using Hangfire;
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
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

public class ZipImportJob(
    DbContextOptions<AppDbContext> dbOptions,
    IDocumentStorage storage,
    IDocumentExtractionQueue extractionQueue,
    IZipImportQueue zipImportQueue,
    ILogger<ZipImportJob> logger)
{
    private static readonly Dictionary<string, string> ZipEntryContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    // A batch left un-Done after all 3 retries are exhausted is not specially recovered
    // here — it simply stays Done=false forever, the same way DocumentExtractionJob
    // leaves a Document stuck at ExtractionStatus.Pending if its own retries run out.
    // Inbox.razor's poll-timer already has a staleness cutoff for exactly this class of
    // problem (see PendingStaleAfter) and gets an equivalent one for batches in Task 7.
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int batchId)
    {
        // This job has no HttpContext, so a DI-resolved ICurrentUser/AppDbContext/DocumentService
        // would always see OrganisationId == null (DocumentService.UploadAsync would reject every
        // entry with "Ingen aktiv organisation."). Instead, build our own AppDbContext bound to a
        // mutable LocalCurrentUser, the same way DemoDataSeeder does — starting with no org (so the
        // initial batch lookup, done with IgnoreQueryFilters, is unaffected either way) and then
        // setting it to the batch's own OrganisationId once known, so DocumentService's writes and
        // this context's own tenant query filter both scope correctly from that point on.
        var tenant = new LocalCurrentUser();
        await using var db = new AppDbContext(dbOptions, tenant);
        var documentService = new DocumentService(db, storage, extractionQueue, zipImportQueue, tenant);

        var batch = await db.ZipImportBatches.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.Id == batchId);
        if (batch is null || batch.Done) return;
        tenant.OrganisationId = batch.OrganisationId;

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite))
            {
                var readStrategy = db.Database.CreateExecutionStrategy();
                await readStrategy.ExecuteAsync(async () =>
                {
                    // A retry re-invokes this whole delegate: reset the temp file so a
                    // prior attempt's partial write (from a transient failure mid-copy)
                    // can't leave leftover bytes ahead of this attempt's data.
                    tempStream.Position = 0;
                    tempStream.SetLength(0);
                    await using var tx = await db.Database.BeginTransactionAsync();
                    var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                    await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, batch.StagingOid!.Value, tempStream);
                    await tx.CommitAsync();
                });
                tempStream.Position = 0;

                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(tempStream, ZipArchiveMode.Read, leaveOpen: true);
                }
                catch (InvalidDataException)
                {
                    await AppendSkippedAsync(batch, "(zip-fil)", "Ogiltig zip-fil.");
                    batch.Done = true;
                    await db.SaveChangesAsync();
                    await DeleteStagingAsync(db, batch);
                    return;
                }

                using (archive)
                {
                    var fileEntries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

                    foreach (var entry in fileEntries.Skip(batch.ProcessedEntries))
                    {
                        if (!ZipEntryContentTypes.TryGetValue(Path.GetExtension(entry.Name), out var contentType))
                        {
                            await AppendSkippedAsync(batch, entry.Name, "Otillåten filtyp.");
                        }
                        else
                        {
                            try
                            {
                                var entryFullName = entry.FullName;
                                var (doc, err) = await documentService.UploadAsync(
                                    entry.Name, contentType, () => archive.GetEntry(entryFullName)!.Open());
                                if (doc is not null)
                                    batch.ImportedCount++;
                                else
                                    await AppendSkippedAsync(batch, entry.Name, err ?? "Okänt fel.");
                            }
                            catch (InvalidDataException)
                            {
                                await AppendSkippedAsync(batch, entry.Name, "Skadad fil.");
                            }
                        }

                        batch.ProcessedEntries++;
                        await db.SaveChangesAsync();
                    }
                }
            }

            batch.Done = true;
            await db.SaveChangesAsync();
            await DeleteStagingAsync(db, batch);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private async Task AppendSkippedAsync(ZipImportBatch batch, string fileName, string reason)
    {
        var skipped = JsonSerializer.Deserialize<List<SkippedEntry>>(batch.SkippedReasonsJson) ?? [];
        skipped.Add(new SkippedEntry(fileName, reason));
        batch.SkippedReasonsJson = JsonSerializer.Serialize(skipped);
        batch.SkippedCount++;
        await Task.CompletedTask;
    }

    private async Task DeleteStagingAsync(AppDbContext db, ZipImportBatch batch)
    {
        if (batch.StagingOid is null) return;

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                await PostgresLargeObjects.DeleteLargeObjectAsync(conn, batch.StagingOid!.Value);
                batch.StagingOid = null;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            });
        }
        catch (Exception ex)
        {
            // The batch has already been marked Done and the local temp file cleaned up —
            // a leaked LO here is a minor storage-cleanup miss, not a correctness issue for
            // the batch itself, so log and move on rather than failing the whole run.
            logger.LogWarning(ex, "Failed to delete staging large object {Oid} for batch {BatchId}", batch.StagingOid, batch.Id);
        }
    }
}
