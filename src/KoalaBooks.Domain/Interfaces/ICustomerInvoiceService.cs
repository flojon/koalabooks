using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface ICustomerInvoiceService
{
    Task<List<CustomerInvoice>> GetAllAsync(int fiscalYearId);
    Task<CustomerInvoice?> GetByIdAsync(int id);
    Task<byte[]?> GetPdfAsync(int id);
    Task<(CustomerInvoice? Invoice, string? Error)> CreateAsync(
        CustomerInvoice invoice, List<CustomerInvoiceLine> lines);
    Task<(CustomerInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId,
        int receivableAccountId,
        int revenueAccountId,
        IReadOnlyDictionary<int, int> vatRateAccountIds);
    Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate);
    Task<(CustomerInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId,
        DateOnly paidDate,
        int bankAccountId,
        int receivableAccountId,
        int? linkBankTransactionId = null);
    Task<string?> DeleteAsync(int invoiceId);
    Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix);
}
