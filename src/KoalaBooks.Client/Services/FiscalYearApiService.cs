using System.Net.Http.Json;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Client.Services;

// HTTP-backed IFiscalYearService for the WASM render tree — EF/Npgsql can't run in the
// browser sandbox, so this calls the REST API (api/v1) instead of AppDbContext.
public class FiscalYearApiService(HttpClient http) : IFiscalYearService
{
    public async Task<List<FiscalYear>> GetAllAsync()
    {
        var years = await http.GetFromJsonAsync<List<FiscalYearResponse>>(
            "api/v1/fiscal-years", ApiJson.Options).ConfigureAwait(false);
        return years?.Select(ToEntity).ToList() ?? [];
    }

    public async Task<FiscalYear?> GetByIdAsync(int id)
    {
        var response = await http.GetAsync($"api/v1/fiscal-years/{id}").ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var year = await response.Content.ReadFromJsonAsync<FiscalYearResponse>(ApiJson.Options).ConfigureAwait(false);
        return year is null ? null : ToEntity(year);
    }

    public async Task<FiscalYear?> GetActiveAsync()
    {
        var response = await http.GetAsync("api/v1/fiscal-years/active").ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var year = await response.Content.ReadFromJsonAsync<FiscalYearResponse>(ApiJson.Options).ConfigureAwait(false);
        return year is null ? null : ToEntity(year);
    }

    public async Task<List<Account>> GetAccountsAsync(int fiscalYearId)
    {
        var accounts = await http.GetFromJsonAsync<List<AccountApiService.AccountResponse>>(
            $"api/v1/fiscal-years/{fiscalYearId}/accounts", ApiJson.Options).ConfigureAwait(false);
        return accounts?.Select(AccountApiService.ToEntity).ToList() ?? [];
    }

    public Task<FiscalYear> CreateAsync(FiscalYear fiscalYear) =>
        throw new NotSupportedException(
            "Fiscal year creation has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    public Task PropagateBalancesToNextYearAsync(int fiscalYearId) =>
        throw new NotSupportedException(
            "Balance propagation has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    private static FiscalYear ToEntity(FiscalYearResponse r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        StartDate = r.StartDate,
        EndDate = r.EndDate,
        IsClosed = r.IsClosed
    };

    private record FiscalYearResponse(int Id, string Name, DateOnly StartDate, DateOnly EndDate, bool IsClosed);
}
