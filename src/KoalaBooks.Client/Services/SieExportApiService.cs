using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

public class SieExportApiService(HttpClient http) : ISieExportService
{
    public async Task<byte[]> ExportAsync(int fiscalYearId, string? companyName = null)
    {
        var url = $"api/v1/fiscal-years/{fiscalYearId}/sie-export";
        if (!string.IsNullOrWhiteSpace(companyName))
            url += $"?companyName={Uri.EscapeDataString(companyName)}";

        var response = await http.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }
}
