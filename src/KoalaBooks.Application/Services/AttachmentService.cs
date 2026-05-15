using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class AttachmentService(AppDbContext db)
{
    public async Task<JournalEntryAttachment?> AddAsync(int entryId, string fileName, string contentType, byte[] data)
    {
        // Verify the entry belongs to the current tenant (query filter applied).
        // EF global filters only guard reads; without this check a caller could
        // write an attachment onto a different tenant's journal entry.
        var entryExists = await db.JournalEntries.AnyAsync(j => j.Id == entryId);
        if (!entryExists) return null;

        var attachment = new JournalEntryAttachment
        {
            JournalEntryId = entryId,
            FileName = fileName,
            ContentType = contentType,
            FileSize = data.Length,
            Data = data,
            UploadedAt = DateTime.UtcNow
        };
        db.JournalEntryAttachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment;
    }

    public async Task<List<AttachmentMeta>> GetMetaAsync(int entryId) =>
        await db.JournalEntryAttachments
            .Where(a => a.JournalEntryId == entryId)
            .OrderBy(a => a.UploadedAt)
            .Select(a => new AttachmentMeta
            {
                Id = a.Id,
                FileName = a.FileName,
                ContentType = a.ContentType,
                FileSize = a.FileSize,
                UploadedAt = a.UploadedAt
            })
            .ToListAsync();

    public async Task<Dictionary<int, int>> GetCountsAsync(IEnumerable<int> entryIds)
    {
        var ids = entryIds.ToList();
        return await db.JournalEntryAttachments
            .Where(a => ids.Contains(a.JournalEntryId))
            .GroupBy(a => a.JournalEntryId)
            .ToDictionaryAsync(g => g.Key, g => g.Count());
    }

    public async Task<JournalEntryAttachment?> GetAsync(int id) =>
        await db.JournalEntryAttachments.FirstOrDefaultAsync(a => a.Id == id);

    public async Task<bool> DeleteAsync(int id)
    {
        var a = await db.JournalEntryAttachments.FirstOrDefaultAsync(att => att.Id == id);
        if (a is null) return false;
        db.JournalEntryAttachments.Remove(a);
        await db.SaveChangesAsync();
        return true;
    }
}

public class AttachmentMeta
{
    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:N1} KB",
        _ => $"{FileSize / (1024.0 * 1024):N1} MB"
    };
}
