using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Domain.Interfaces;

public record BankFileParseResult(
    bool Success,
    string? Error,
    List<string> Headers,
    List<string[]> DataRows);

public record BankTransactionPreview(
    int RowIndex,
    DateOnly? Date,
    decimal? Amount,
    string Description,
    string? Reference,
    bool IsDuplicate,
    string? ParseError);

public record BankImportResult(int Imported, int Skipped, int Duplicates, List<string> Errors);

public interface IBankImportService
{
    BankFileParseResult ParseFile(Stream stream, string fileName);
    Task<List<BankTransactionPreview>> BuildPreviewAsync(
        int accountId,
        List<string[]> rows,
        int dateCol,
        int amountCol,
        int descCol,
        int? refCol,
        string dateFormat);
    Task<BankImportResult> ImportAsync(int accountId, List<BankTransactionPreview> previews);
    Task<int> CountUnmatchedAsync(int fiscalYearId);
    Task<List<BankTransaction>> GetUnmatchedAsync(int fiscalYearId);
    Task<int> CountUnmatchedForOrganisationAsync(int organisationId);
    Task<List<BankTransaction>> GetUnmatchedForOrganisationAsync(int organisationId);
    Task<List<BankTransaction>> GetByAccountAsync(int accountId);
    Task<List<BankTransaction>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from, DateOnly? to, int? accountId);
    Task<BankTransaction?> GetByIdAsync(int id);
    Task<List<Account>> GetImportableAccountsAsync(int fiscalYearId, string prefix);
    Task SetStatusAsync(int bankTransactionId, BankTransactionStatus status);
    Task<string?> MatchToEntryAsync(int bankTransactionId, int journalEntryId);
    Task<List<JournalEntry>> GetUnmatchedJournalEntriesAsync(int fiscalYearId, int bankAccountId);
    Task<int?> SuggestContraAccountAsync(int bankAccountId, string description, decimal amount);
}
