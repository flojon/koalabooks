// src/KoalaBooks.Infrastructure/Services/DbDocumentStorage.cs
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Services;

public class DbDocumentStorage(AppDbContext db) : IDocumentStorage
{
    public async Task<string> SaveAsync(int documentId, string contentType, byte[] data)
    {
        var existing = await db.DocumentData.FindAsync(documentId);
        if (existing is not null)
        {
            existing.Data = data;
        }
        else
        {
            db.DocumentData.Add(new DocumentData { DocumentId = documentId, Data = data });
        }
        await db.SaveChangesAsync();
        return documentId.ToString();
    }

    public async Task<byte[]> LoadAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return [];
        var row = await db.DocumentData.FindAsync(id);
        return row?.Data ?? [];
    }

    public async Task DeleteAsync(string storageKey)
    {
        if (!int.TryParse(storageKey, out var id)) return;
        var row = await db.DocumentData.FindAsync(id);
        if (row is not null)
        {
            db.DocumentData.Remove(row);
            await db.SaveChangesAsync();
        }
    }
}
