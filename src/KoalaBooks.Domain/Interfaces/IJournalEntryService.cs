using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Interfaces;

public interface IJournalEntryService
{
    Task<PagedResult<JournalEntry>> GetByFiscalYearAsync(
        int fiscalYearId, DateOnly? from = null, DateOnly? to = null,
        string? search = null,
        JournalEntrySortBy sortBy = JournalEntrySortBy.EntryNumber,
        int page = 1, int pageSize = 50);
    Task<int> CountDraftsAsync(int fiscalYearId);
    Task<List<JournalEntry>> GetDraftsForOrganisationAsync();
    Task<int> CountDraftsForOrganisationAsync();
    Task<JournalEntry?> GetByIdAsync(int id);
    Task<(JournalEntry? Entry, string? Error)> CreateAsync(JournalEntry entry);
    Task<(List<JournalEntry> Created, string? Error, int? FailedEntryIndex)> CreateManyAsync(int fiscalYearId, List<JournalEntry> entries);
    Task<(JournalEntry? Entry, string? Error)> UpdateAsync(JournalEntry entry);
    Task<string?> PostAsync(int entryId);
    Task<string?> DeleteDraftAsync(int entryId);
    Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason);
    Task<(JournalEntry? Preview, string? Error)> PreviewReversalAsync(int entryId, string reason);
}
