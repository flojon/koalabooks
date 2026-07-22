using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KoalaBooks.Application.Services;

public class SieImportUploadService(
    AppDbContext db,
    ISieImportService sieImportService,
    IBackgroundJobRunService backgroundJobRunService,
    ISieImportQueue sieImportQueue,
    ICurrentUser currentUser) : ISieImportUploadService
{
    public async Task<(SieImportPreview? Preview, string? Error)> PreviewAsync(Stream sieFileStream)
    {
        try
        {
            var doc = sieImportService.Parse(sieFileStream);
            var preview = await sieImportService.GetPreviewAsync(doc).ConfigureAwait(false);
            return (preview, null);
        }
        catch (Exception)
        {
            return (null, "Ogiltig SIE-fil.");
        }
    }

    public async Task<(int? RunId, string? Error)> EnqueueImportAsync(
        string fileName, Func<Stream> openSieFileData, bool overwrite, int? rarId)
    {
        if (currentUser.OrganisationId is null)
            return (null, "Ingen aktiv organisation.");

        // Fail fast on an unparseable file before staging any bytes.
        try
        {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using var validateStream = openSieFileData();
#pragma warning restore CA2007
            sieImportService.Parse(validateStream);
        }
        catch (Exception)
        {
            return (null, "Ogiltig SIE-fil.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var (runId, stagingOid) = await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this whole delegate: a prior failed attempt may have left a
            // BackgroundJobRun row tracked (Added) without committing — detach it before
            // adding a fresh one, or SaveChangesAsync would insert both and produce a
            // duplicate row. Mirrors DocumentService.UploadZipAsync.
            foreach (var stale in db.ChangeTracker.Entries<BackgroundJobRun>().Where(e => e.State == EntityState.Added).ToList())
                stale.State = EntityState.Detached;

#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using var source = openSieFileData();
#pragma warning restore CA2007
            var (oid, _) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, source).ConfigureAwait(false);

            var run = await backgroundJobRunService.CreateRunAsync(BackgroundJobType.SieImport).ConfigureAwait(false);

            await tx.CommitAsync().ConfigureAwait(false);
            return (run.Id, oid);
        }).ConfigureAwait(false);

        sieImportQueue.Enqueue(runId, fileName, stagingOid, overwrite, rarId);
        return (runId, null);
    }
}
