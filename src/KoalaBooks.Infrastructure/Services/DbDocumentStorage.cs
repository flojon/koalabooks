// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

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

            await using var data = openData();

            try
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var conn = (NpgsqlConnection)db.Database.GetDbConnection();

                var existing = await db.DocumentData.FindAsync(documentId);
                if (existing is not null)
                    await PostgresLargeObjects.DeleteLargeObjectAsync(conn, existing.Oid);

                var (oid, fileSize) = await PostgresLargeObjects.CopyStreamIntoNewLargeObjectAsync(conn, data);

                if (existing is not null)
                    existing.Oid = oid;
                else
                    db.DocumentData.Add(new DocumentData { DocumentId = documentId, Oid = oid });

                await db.SaveChangesAsync();
                await tx.CommitAsync();
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
        });
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var row = await db.DocumentData.FindAsync(id);
            if (row is null) return [];

            var conn = (NpgsqlConnection)db.Database.GetDbConnection();
            using var ms = new MemoryStream();
            await PostgresLargeObjects.CopyLargeObjectIntoStreamAsync(conn, row.Oid, ms);
            await tx.CommitAsync();
            return ms.ToArray();
        });
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
                await using var tx = await db.Database.BeginTransactionAsync();
                var row = await db.DocumentData.FindAsync(id);
                if (row is null) return;

                var conn = (NpgsqlConnection)db.Database.GetDbConnection();
                await PostgresLargeObjects.DeleteLargeObjectAsync(conn, row.Oid);
                db.DocumentData.Remove(row);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
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
        });
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
