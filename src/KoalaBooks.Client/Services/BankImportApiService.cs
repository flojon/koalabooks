using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

// HTTP-backed IBankImportService for the WASM render tree — MainLayout only needs the
// unmatched-count nav badge; everything else has no REST endpoint and isn't needed by
// the WASM-rendered /review page.
public class BankImportApiService(HttpClient http) : IBankImportService
{
    public async Task<int> CountUnmatchedAsync(int fiscalYearId)
    {
        var response = await http.GetFromJsonAsync<CountResponse>(
            $"api/v1/fiscal-years/{fiscalYearId}/bank-transactions/unmatched-count", ApiJson.Options).ConfigureAwait(false);
        return response?.Count ?? 0;
    }

    // organisationId is not sent to the server — the endpoint resolves the tenant from
    // ICurrentUser, so a caller can't use this to look up another organisation's count.
    public async Task<int> CountUnmatchedForOrganisationAsync(int organisationId)
    {
        var response = await http.GetFromJsonAsync<CountResponse>(
            "api/v1/bank-transactions/unmatched-count", ApiJson.Options).ConfigureAwait(false);
        return response?.Count ?? 0;
    }

    public Task<List<BankTransaction>> GetUnmatchedForOrganisationAsync(int organisationId) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Fetching organisation-wide unmatched bank transactions has no REST endpoint yet."));

    // Everything below has no REST endpoint yet and isn't needed by the WASM-rendered /review
    // page. Task-returning members use Task.FromException so the failure surfaces on await like
    // a real async call, instead of throwing synchronously at the call site.
    public BankFileParseResult ParseFile(Stream stream, string fileName) =>
        throw new NotSupportedException("Bank file parsing has no REST endpoint yet.");

    public Task<List<BankTransactionPreview>> BuildPreviewAsync(
        int accountId, List<string[]> rows, int dateCol, int amountCol, int descCol, int? refCol, string dateFormat) =>
        Task.FromException<List<BankTransactionPreview>>(
            new NotSupportedException("Bank import preview has no REST endpoint yet."));

    public Task<BankImportResult> ImportAsync(int accountId, List<BankTransactionPreview> previews) =>
        Task.FromException<BankImportResult>(
            new NotSupportedException("Bank import has no REST endpoint yet."));

    public Task<List<BankTransaction>> GetUnmatchedAsync(int fiscalYearId) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Fetching unmatched bank transactions has no REST endpoint yet."));

    public Task<List<BankTransaction>> GetByAccountAsync(int accountId) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Fetching bank transactions by account has no REST endpoint yet."));

    public Task<List<BankTransaction>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from, DateOnly? to, int? accountId) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Fetching bank transactions by fiscal year has no REST endpoint yet."));

    public Task<BankTransaction?> GetByIdAsync(int id) =>
        Task.FromException<BankTransaction?>(
            new NotSupportedException("Fetching a bank transaction by id has no REST endpoint yet."));

    public Task<List<Account>> GetImportableAccountsAsync(int fiscalYearId, string prefix) =>
        Task.FromException<List<Account>>(
            new NotSupportedException("Fetching importable accounts has no REST endpoint yet."));

    public Task SetStatusAsync(int bankTransactionId, BankTransactionStatus status) =>
        Task.FromException(
            new NotSupportedException("Setting bank transaction status has no REST endpoint yet."));

    public Task<string?> MatchToEntryAsync(int bankTransactionId, int journalEntryId) =>
        Task.FromException<string?>(
            new NotSupportedException("Matching a bank transaction to an entry has no REST endpoint yet."));

    public Task<List<JournalEntry>> GetUnmatchedJournalEntriesAsync(int fiscalYearId, int bankAccountId) =>
        Task.FromException<List<JournalEntry>>(
            new NotSupportedException("Fetching unmatched journal entries has no REST endpoint yet."));

    public Task<int?> SuggestContraAccountAsync(int bankAccountId, string description, decimal amount) =>
        Task.FromException<int?>(
            new NotSupportedException("Contra account suggestion has no REST endpoint yet."));

    private record CountResponse(int Count);
}
