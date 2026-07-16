using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Application.Services;

public interface IJournalEntryService
{
    Task<List<JournalEntry>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null);
    Task<int> CountDraftsAsync(int fiscalYearId);
    Task<JournalEntry?> GetByIdAsync(int id);
    Task<(JournalEntry? Entry, string? Error)> CreateAsync(JournalEntry entry);
    Task<(JournalEntry? Entry, string? Error)> UpdateAsync(JournalEntry entry);
    Task<string?> PostAsync(int entryId);
    Task<string?> DeleteDraftAsync(int entryId);
    Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason);
}
