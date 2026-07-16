using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Application.Services;

public interface ISupplierInvoiceService
{
    Task<int> CountUnpaidAsync(int fiscalYearId);
    Task<List<SupplierInvoice>> GetAllAsync(int fiscalYearId);
    Task<(SupplierInvoice? Invoice, string? Error)> CreateAsync(SupplierInvoice invoice);
    Task<(SupplierInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int expenseAccountId, int payableAccountId, int? vatAccountId);
    Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate);
    Task<(SupplierInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int payableAccountId,
        int? linkBankTransactionId = null);
    Task<string?> DeleteAsync(int invoiceId);
    Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix);
    Task<List<string>> GetSuppliersAsync(int fiscalYearId);
    Task<HashSet<int>> GetLinkedJournalEntryIdsAsync(int fiscalYearId);
    Task<List<JournalEntry>> GetLinkableEntriesAsync(int fiscalYearId);
    Task<(SupplierInvoice? Invoice, string? Error)> CreateFromEntryAsync(
        int journalEntryId,
        SupplierInvoice invoice);
}
