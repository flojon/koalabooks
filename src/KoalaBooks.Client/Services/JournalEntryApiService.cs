using System.Net.Http.Json;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Client.Services;

// HTTP-backed IJournalEntryService for the WASM render tree — EF/Npgsql can't run in the
// browser sandbox, so this calls the REST API (api/v1) instead of AppDbContext.
public class JournalEntryApiService(HttpClient http) : IJournalEntryService
{
    public async Task<List<JournalEntry>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from = null, DateOnly? to = null)
    {
        var query = BuildDateRangeQuery(from, to);
        var page = await http.GetFromJsonAsync<PagedResult<JournalEntryResponse>>(
            $"api/v1/fiscal-years/{fiscalYearId}/journal-entries?pageSize=200{query}", ApiJson.Options)
            .ConfigureAwait(false);
        return page?.Items.Select(ToEntity).ToList() ?? [];
    }

    public async Task<int> CountDraftsAsync(int fiscalYearId)
    {
        var count = await http.GetFromJsonAsync<CountResponse>(
            $"api/v1/fiscal-years/{fiscalYearId}/journal-entries/draft-count", ApiJson.Options).ConfigureAwait(false);
        return count?.Count ?? 0;
    }

    public async Task<List<JournalEntry>> GetDraftsForOrganisationAsync(int organisationId)
    {
        var drafts = await http.GetFromJsonAsync<List<JournalEntryResponse>>(
            "api/v1/journal-entries/drafts", ApiJson.Options).ConfigureAwait(false);
        return drafts?.Select(ToEntity).ToList() ?? [];
    }

    public async Task<int> CountDraftsForOrganisationAsync(int organisationId)
    {
        var count = await http.GetFromJsonAsync<CountResponse>(
            "api/v1/journal-entries/draft-count", ApiJson.Options).ConfigureAwait(false);
        return count?.Count ?? 0;
    }

    public async Task<JournalEntry?> GetByIdAsync(int id)
    {
        var response = await http.GetAsync($"api/v1/journal-entries/{id}").ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var entry = await response.Content.ReadFromJsonAsync<JournalEntryResponse>(ApiJson.Options).ConfigureAwait(false);
        return entry is null ? null : ToEntity(entry);
    }

    public async Task<(JournalEntry? Entry, string? Error)> CreateAsync(JournalEntry entry)
    {
        var request = new CreateOrUpdateRequest(entry.Date, entry.Description, entry.Lines.Select(ToLineRequest).ToList());
        var response = await http.PostAsJsonAsync(
            $"api/v1/fiscal-years/{entry.FiscalYearId}/journal-entries", request, ApiJson.Options).ConfigureAwait(false);
        return await ToResultAsync(response).ConfigureAwait(false);
    }

    public async Task<(JournalEntry? Entry, string? Error)> UpdateAsync(JournalEntry entry)
    {
        var request = new CreateOrUpdateRequest(entry.Date, entry.Description, entry.Lines.Select(ToLineRequest).ToList());
        var response = await http.PutAsJsonAsync(
            $"api/v1/journal-entries/{entry.Id}", request, ApiJson.Options).ConfigureAwait(false);
        return await ToResultAsync(response).ConfigureAwait(false);
    }

    public async Task<string?> PostAsync(int entryId)
    {
        var response = await http.PostAsync($"api/v1/journal-entries/{entryId}/post", content: null).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? null : await ApiJson.ReadErrorAsync(response).ConfigureAwait(false);
    }

    public async Task<string?> DeleteDraftAsync(int entryId)
    {
        var response = await http.DeleteAsync($"api/v1/journal-entries/{entryId}").ConfigureAwait(false);
        return response.IsSuccessStatusCode ? null : await ApiJson.ReadErrorAsync(response).ConfigureAwait(false);
    }

    public async Task<(JournalEntry? Entry, string? Error)> CreateReversalAsync(int entryId, string reason)
    {
        var response = await http.PostAsJsonAsync(
            $"api/v1/journal-entries/{entryId}/reverse", new ReverseRequest(reason), ApiJson.Options).ConfigureAwait(false);
        return await ToResultAsync(response).ConfigureAwait(false);
    }

    public Task<(JournalEntry? Preview, string? Error)> PreviewReversalAsync(int entryId, string reason) =>
        throw new NotSupportedException(
            "Reversal preview has no REST endpoint yet; not needed by the WASM-rendered /review page.");

    private async Task<(JournalEntry? Entry, string? Error)> ToResultAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var entry = await response.Content.ReadFromJsonAsync<JournalEntryResponse>(ApiJson.Options).ConfigureAwait(false);
        return (entry is null ? null : ToEntity(entry), null);
    }

    private static string BuildDateRangeQuery(DateOnly? from, DateOnly? to)
    {
        var parts = new List<string>();
        if (from is not null) parts.Add($"from={from:yyyy-MM-dd}");
        if (to is not null) parts.Add($"to={to:yyyy-MM-dd}");
        return parts.Count == 0 ? "" : "&" + string.Join("&", parts);
    }

    private static CreateOrUpdateLineRequest ToLineRequest(JournalEntryLine l) =>
        new(l.AccountId, l.DebitAmount, l.CreditAmount);

    private static JournalEntry ToEntity(JournalEntryResponse r) => new()
    {
        Id = r.Id,
        EntryNumber = r.EntryNumber,
        Date = r.Date,
        Description = r.Description,
        IsPosted = r.IsPosted,
        Status = r.Status,
        SourceJournalEntryId = r.SourceJournalEntryId,
        CreatedAt = r.CreatedAt,
        Lines = r.Lines.Select(l => new JournalEntryLine
        {
            Id = l.Id,
            AccountId = l.AccountId,
            DebitAmount = l.DebitAmount,
            CreditAmount = l.CreditAmount,
            Account = new Account
            {
                Id = l.AccountId,
                AccountNumber = l.AccountNumber,
                Name = l.AccountName
            }
        }).ToList()
    };

    private record PagedResult<T>(List<T> Items, int Page, int PageSize, int TotalCount);

    private record CountResponse(int Count);

    private record CreateOrUpdateRequest(DateOnly Date, string Description, List<CreateOrUpdateLineRequest> Lines);

    private record CreateOrUpdateLineRequest(int AccountId, decimal DebitAmount, decimal CreditAmount);

    private record ReverseRequest(string Reason);

    private record JournalEntryResponse(
        int Id,
        int EntryNumber,
        DateOnly Date,
        string Description,
        bool IsPosted,
        JournalEntryStatus Status,
        int? SourceJournalEntryId,
        DateTime CreatedAt,
        List<JournalEntryLineResponse> Lines);

    private record JournalEntryLineResponse(
        int Id, int AccountId, string AccountNumber, string AccountName, decimal DebitAmount, decimal CreditAmount);
}
