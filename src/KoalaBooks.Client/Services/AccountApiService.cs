using System.Net.Http.Json;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Client.Services;

// HTTP-backed IAccountService for the WASM render tree — EF/Npgsql can't run in the
// browser sandbox, so this calls the REST API (api/v1) instead of AppDbContext.
public class AccountApiService(HttpClient http) : IAccountService
{
    public async Task<List<Account>> GetAllAsync(int fiscalYearId)
    {
        var accounts = await http.GetFromJsonAsync<List<AccountResponse>>(
            $"api/v1/fiscal-years/{fiscalYearId}/accounts", ApiJson.Options).ConfigureAwait(false);
        return accounts?.Select(ToEntity).ToList() ?? [];
    }

    public async Task<Account?> GetByIdAsync(int id)
    {
        var response = await http.GetAsync($"api/v1/accounts/{id}").ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var account = await response.Content.ReadFromJsonAsync<AccountResponse>(ApiJson.Options).ConfigureAwait(false);
        return account is null ? null : ToEntity(account);
    }

    public Task<Account> CreateAsync(Account account) =>
        throw new NotSupportedException(
            "Account creation has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    public Task UpdateAsync(Account account) =>
        throw new NotSupportedException(
            "Account update has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    public Task ToggleActiveAsync(int id) =>
        throw new NotSupportedException(
            "Toggling account active state has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    public Task<List<Account>> GetMissingFromSourceAsync(int currentFiscalYearId, int sourceFiscalYearId) =>
        throw new NotSupportedException(
            "Cross-year account comparison has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    public Task<int> CopyAccountsAsync(int targetFiscalYearId, List<int> sourceAccountIds) =>
        throw new NotSupportedException(
            "Copying accounts has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    internal static Account ToEntity(AccountResponse r) => new()
    {
        Id = r.Id,
        AccountNumber = r.AccountNumber,
        Name = r.Name,
        AccountClass = r.AccountClass,
        IsActive = r.IsActive,
        IncomingBalance = r.IncomingBalance,
        OutgoingBalance = r.OutgoingBalance
    };

    internal record AccountResponse(
        int Id,
        string AccountNumber,
        string Name,
        AccountClass AccountClass,
        bool IsActive,
        decimal IncomingBalance,
        decimal OutgoingBalance);
}
