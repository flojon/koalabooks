using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

public class CustomerInvoiceApiService(HttpClient http) : ICustomerInvoiceService
{
    public async Task<List<CustomerInvoice>> GetAllAsync(int fiscalYearId)
    {
        var result = await http.GetFromJsonAsync<PagedResult>(
            $"api/v1/fiscal-years/{fiscalYearId}/customer-invoices?pageSize=200", ApiJson.Options).ConfigureAwait(false);
        return result?.Items ?? [];
    }

    public async Task<CustomerInvoice?> GetByIdAsync(int id) =>
        await http.GetFromJsonAsync<CustomerInvoice>($"api/v1/customer-invoices/{id}", ApiJson.Options).ConfigureAwait(false);

    public async Task<byte[]?> GetPdfAsync(int id)
    {
        var response = await http.GetAsync($"api/v1/customer-invoices/{id}/pdf").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    public async Task<(CustomerInvoice? Invoice, string? Error)> CreateAsync(
        CustomerInvoice invoice, List<CustomerInvoiceLine> lines)
    {
        var payload = new
        {
            invoice.CustomerId,
            invoice.CustomerName,
            invoice.InvoiceDate,
            invoice.DueDate,
            invoice.OurReference,
            invoice.YourReference,
            invoice.Notes,
            Lines = lines.Select(l => new { l.Description, l.Quantity, l.UnitPrice, l.VatRate })
        };
        var response = await http.PostAsJsonAsync(
            $"api/v1/fiscal-years/{invoice.FiscalYearId}/customer-invoices", payload, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var created = await response.Content.ReadFromJsonAsync<CustomerInvoice>(ApiJson.Options).ConfigureAwait(false);
        return (created, null);
    }

    public async Task<(CustomerInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int receivableAccountId, int revenueAccountId, IReadOnlyDictionary<int, int> vatRateAccountIds)
    {
        var payload = new { ReceivableAccountId = receivableAccountId, RevenueAccountId = revenueAccountId, VatRateAccountIds = vatRateAccountIds };
        var response = await http.PostAsJsonAsync($"api/v1/customer-invoices/{invoiceId}/post", payload, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var posted = await response.Content.ReadFromJsonAsync<CustomerInvoice>(ApiJson.Options).ConfigureAwait(false);
        return (posted, null);
    }

    public async Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate)
    {
        var url = $"api/v1/fiscal-years/{fiscalYearId}/customer-invoices/find-matching-bank-tx" +
                  $"?invoiceTotal={invoiceTotal}&invoiceDate={invoiceDate:yyyy-MM-dd}&dueDate={dueDate:yyyy-MM-dd}";
        var result = await http.GetFromJsonAsync<List<BankTransactionMatchDto>>(url, ApiJson.Options).ConfigureAwait(false);

        // The endpoint returns BankTransactionResponse shape (flat AccountNumber string,
        // no Account nav, no OrganisationId/ImportedAt) — deserializing straight into
        // BankTransaction would silently leave the required Account nav null. Map by hand
        // instead; Account.Name isn't in the DTO so it's approximated from the number.
        return (result ?? []).Select(b => new BankTransaction
        {
            Id = b.Id,
            AccountId = b.AccountId,
            Account = new Account { AccountNumber = b.AccountNumber, Name = b.AccountNumber },
            Date = b.Date,
            Amount = b.Amount,
            Description = b.Description,
            Reference = b.Reference,
            Status = b.Status,
            JournalEntryId = b.JournalEntryId
        }).ToList();
    }

    private record BankTransactionMatchDto(
        int Id, int AccountId, string AccountNumber, DateOnly Date, decimal Amount,
        string Description, string? Reference, BankTransactionStatus Status, int? JournalEntryId);

    public async Task<(CustomerInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId, DateOnly paidDate, int bankAccountId, int receivableAccountId, int? linkBankTransactionId = null)
    {
        var payload = new
        {
            PaidDate = paidDate,
            BankAccountId = bankAccountId,
            ReceivableAccountId = receivableAccountId,
            LinkBankTransactionId = linkBankTransactionId
        };
        var response = await http.PostAsJsonAsync($"api/v1/customer-invoices/{invoiceId}/mark-paid", payload, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var paid = await response.Content.ReadFromJsonAsync<CustomerInvoice>(ApiJson.Options).ConfigureAwait(false);
        return (paid, null);
    }

    public async Task<string?> DeleteAsync(int invoiceId)
    {
        var response = await http.DeleteAsync($"api/v1/customer-invoices/{invoiceId}").ConfigureAwait(false);
        return response.IsSuccessStatusCode ? null : await ApiJson.ReadErrorAsync(response).ConfigureAwait(false);
    }

    public Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix) =>
        Task.FromException<Account?>(
            new NotSupportedException("Finding an account by prefix has no REST endpoint yet."));

    private record PagedResult(List<CustomerInvoice> Items, int Page, int PageSize, int TotalCount);
}
