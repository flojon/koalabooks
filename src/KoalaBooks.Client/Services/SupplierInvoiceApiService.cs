using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

// HTTP-backed ISupplierInvoiceService for the WASM render tree — MainLayout only needs
// the unpaid-count nav badge; everything else has no REST endpoint and isn't needed by
// the WASM-rendered /review page.
public class SupplierInvoiceApiService(HttpClient http) : ISupplierInvoiceService
{
    public async Task<int> CountUnpaidAsync(int fiscalYearId)
    {
        var response = await http.GetFromJsonAsync<CountResponse>(
            $"api/v1/fiscal-years/{fiscalYearId}/supplier-invoices/unpaid-count", ApiJson.Options).ConfigureAwait(false);
        return response?.Count ?? 0;
    }

    public async Task<int> CountUnpaidForOrganisationAsync(int organisationId)
    {
        var response = await http.GetFromJsonAsync<CountResponse>(
            "api/v1/supplier-invoices/unpaid-count", ApiJson.Options).ConfigureAwait(false);
        return response?.Count ?? 0;
    }

    public Task<List<SupplierInvoice>> GetAllForOrganisationAsync(int organisationId) =>
        Task.FromException<List<SupplierInvoice>>(
            new NotSupportedException("Fetching organisation-wide supplier invoices has no REST endpoint yet."));

    // Everything below has no REST endpoint yet and isn't needed by the WASM-rendered /review
    // page. Task.FromException surfaces the failure on await, like a real async call, instead of
    // throwing synchronously at the call site.
    public Task<List<SupplierInvoice>> GetAllAsync(int fiscalYearId) =>
        Task.FromException<List<SupplierInvoice>>(
            new NotSupportedException("Fetching supplier invoices has no REST endpoint yet."));

    public Task<SupplierInvoice?> GetByIdAsync(int id) =>
        Task.FromException<SupplierInvoice?>(
            new NotSupportedException("Fetching a supplier invoice by id has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> UpdateAsync(SupplierInvoice invoice) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Updating a supplier invoice has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> CreateAsync(SupplierInvoice invoice) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Creating a supplier invoice has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int expenseAccountId, int payableAccountId, int? vatAccountId) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Posting a supplier invoice has no REST endpoint yet."));

    public Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate) =>
        Task.FromException<List<BankTransaction>>(
            new NotSupportedException("Finding matching bank transactions has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int payableAccountId,
        int? linkBankTransactionId = null) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Marking a supplier invoice as paid has no REST endpoint yet."));

    public Task<string?> DeleteAsync(int invoiceId) =>
        Task.FromException<string?>(
            new NotSupportedException("Deleting a supplier invoice has no REST endpoint yet."));

    public Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix) =>
        Task.FromException<Account?>(
            new NotSupportedException("Finding an account by prefix has no REST endpoint yet."));

    public Task<List<string>> GetSuppliersAsync(int fiscalYearId) =>
        Task.FromException<List<string>>(
            new NotSupportedException("Fetching supplier names has no REST endpoint yet."));

    public Task<HashSet<int>> GetLinkedJournalEntryIdsAsync(int fiscalYearId) =>
        Task.FromException<HashSet<int>>(
            new NotSupportedException("Fetching linked journal entry ids has no REST endpoint yet."));

    public Task<List<JournalEntry>> GetLinkableEntriesAsync(int fiscalYearId) =>
        Task.FromException<List<JournalEntry>>(
            new NotSupportedException("Fetching linkable journal entries has no REST endpoint yet."));

    public Task<(SupplierInvoice? Invoice, string? Error)> CreateFromEntryAsync(
        int journalEntryId,
        SupplierInvoice invoice) =>
        Task.FromException<(SupplierInvoice?, string?)>(
            new NotSupportedException("Creating a supplier invoice from an entry has no REST endpoint yet."));

    private record CountResponse(int Count);
}
