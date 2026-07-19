// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KoalaBooks.Infrastructure.Services;

public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    public async Task<(string StorageKey, long FileSize)> SaveAsync(int documentId, string contentType, Func<Stream> openData)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this whole delegate: a prior failed attempt may
            // have left a DocumentData row tracked (Added/Modified) without
            // committing — detach just that row before re-reading it. db is a
            // shared, caller-owned AppDbContext, so this must not touch
            // entities outside our own.
            DetachTrackedDocumentData(documentId);

#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using var data = openData();
#pragma warning restore CA2007

            try
            {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();

                var existing = await db.DocumentData.FindAsync(documentId).ConfigureAwait(false);
                if (existing is not null)
                    await PostgresLargeObjects.DeleteLargeObjectAsync(conn, existing.Oid).ConfigureAwait(false);

                var (oid, fileSize) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, data).ConfigureAwait(false);

                if (existing is not null)
                    existing.Oid = oid;
                else
                    db.DocumentData.Add(new DocumentData { DocumentId = documentId, Oid = oid });

                await db.SaveChangesAsync().ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
                return (documentId.ToString(), fileSize);
            }
            catch
            {
                // A thrown exception leaves this attempt's tracked DocumentData behind
                // even though the DB rolled back — detach it so the caller's context
                // isn't left in an inconsistent state (this matters most when the
                // execution strategy has exhausted all retries and rethrows to the
                // caller, since no further attempt will run the start-of-attempt detach).
                DetachTrackedDocumentData(documentId);
                throw;
            }
        }).ConfigureAwait(false);
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
            var row = await db.DocumentData.FindAsync(id).ConfigureAwait(false);
            if (row is null) return [];

            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            using var ms = new MemoryStream();
            await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, row.Oid, ms).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
            return ms.ToArray();
        }).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            DetachTrackedDocumentData(id);

            try
            {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
                await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007
                var row = await db.DocumentData.FindAsync(id).ConfigureAwait(false);
                if (row is null) return;

                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                await PostgresLargeObjects.DeleteLargeObjectAsync(conn, row.Oid).ConfigureAwait(false);
                db.DocumentData.Remove(row);
                await db.SaveChangesAsync().ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                // A thrown exception leaves this attempt's tracked DocumentData behind
                // even though the DB rolled back — detach it so the caller's context
                // isn't left in an inconsistent state (this matters most when the
                // execution strategy has exhausted all retries and rethrows to the
                // caller, since no further attempt will run the start-of-attempt detach).
                DetachTrackedDocumentData(id);
                throw;
            }
        }).ConfigureAwait(false);
    }

    // Detaches only a stale DocumentData entry left tracked by a previous,
    // retried attempt of this same call — never touches unrelated entities
    // tracked by the caller on this shared AppDbContext.
    private void DetachTrackedDocumentData(int documentId)
    {
        var entry = db.ChangeTracker.Entries<DocumentData>()
            .FirstOrDefault(e => e.Entity.DocumentId == documentId);
        if (entry is not null) entry.State = EntityState.Detached;
    }
}
