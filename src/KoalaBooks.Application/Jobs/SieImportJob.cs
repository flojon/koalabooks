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

namespace KoalaBooks.Application.Jobs;

// BackgroundJobRun.ResultJson payload for a SieImport run — either an all-fiscal-years
// import (rarId is null) or a single-fiscal-year import (rarId set). Mirrors
// ZipImportResult: read by a future status-poll consumer via plain JSON property matching.
public record SieImportJobResult(
    string FileName,
    bool? Overwrite,
    int? RarId,
    SieImportAllResult? AllResult,
    SieImportResult? FiscalYearResult);

public class SieImportJob(
    DbContextOptions<AppDbContext> dbOptions,
    ILogger<SieImportJob> logger) : BackgroundJobRunBase(dbOptions)
{
    // fileName/stagingOid/overwrite/rarId are plain Hangfire job arguments, replayed
    // unchanged on every retry — see ZipImportJob for why. context is Hangfire's
    // PerformContext, substituted at execution time; tests calling RunAsync directly fall
    // back to a fresh random id.
    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(int runId, string fileName, uint stagingOid, bool overwrite, int? rarId, PerformContext? context = null)
    {
        var jobId = context?.BackgroundJob.Id ?? Guid.NewGuid().ToString();
        if (!await LoadRunAsync(runId, jobId).ConfigureAwait(false)) return;

        var sieImportService = new KoalaBooks.Infrastructure.Services.SieImportService(Db, Tenant);

        var tempPath = Path.GetTempFileName();
        try
        {
            var readStrategy = Db.Database.CreateExecutionStrategy();
            await readStrategy.ExecuteAsync(async () =>
            {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite);
#pragma warning restore CA2007
                // A retry re-invokes this whole delegate: reset the temp file so a prior
                // attempt's partial write can't leave leftover bytes ahead of this one.
                tempStream.Position = 0;
                tempStream.SetLength(0);
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await Db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var conn = (NpgsqlConnection)Db.Database.GetDbConnection();
                await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, stagingOid, tempStream).ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);

            SieImportAllResult? allResult = null;
            SieImportResult? fyResult = null;

#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using (var readStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read))
#pragma warning restore CA2007
            {
                var doc = sieImportService.Parse(readStream);

                if (rarId is null)
                    allResult = await sieImportService.ImportAllAsync(doc, overwrite).ConfigureAwait(false);
                else
                    fyResult = await sieImportService.ImportFiscalYearAsync(doc, rarId.Value, overwrite).ConfigureAwait(false);
            }

            await CompleteAsync(BackgroundJobStatus.Completed,
                new SieImportJobResult(fileName, overwrite, rarId, allResult, fyResult)).ConfigureAwait(false);
            await DeleteStagingAsync(stagingOid, runId).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

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
            // Same trade-off as ZipImportJob: the run is already Completed and cleanup of
            // a leaked large object is a minor storage miss, not a correctness issue.
            logger.LogWarning(ex, "Failed to delete staging large object {Oid} for run {RunId}", stagingOid, runId);
        }
    }
}
