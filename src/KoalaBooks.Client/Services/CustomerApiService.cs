using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

public class CustomerApiService(HttpClient http) : ICustomerService
{
    public async Task<List<Customer>> GetAllAsync(int organisationId)
    {
        // organisationId is resolved server-side from the bearer token's tenant claim,
        // same as CustomersController — the parameter is kept only to satisfy the shared
        // interface (Server's CustomerService still uses it for the direct EF query).
        var result = await http.GetFromJsonAsync<List<Customer>>("api/v1/customers", ApiJson.Options).ConfigureAwait(false);
        return result ?? [];
    }

    public async Task<Customer?> GetByIdAsync(int id) =>
        await http.GetFromJsonAsync<Customer>($"api/v1/customers/{id}", ApiJson.Options).ConfigureAwait(false);

    public async Task<(Customer? Customer, string? Error)> CreateAsync(Customer customer)
    {
        var response = await http.PostAsJsonAsync("api/v1/customers", customer, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var created = await response.Content.ReadFromJsonAsync<Customer>(ApiJson.Options).ConfigureAwait(false);
        return (created, null);
    }

    public async Task<(Customer? Customer, string? Error)> UpdateAsync(Customer customer)
    {
        var response = await http.PutAsJsonAsync($"api/v1/customers/{customer.Id}", customer, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var updated = await response.Content.ReadFromJsonAsync<Customer>(ApiJson.Options).ConfigureAwait(false);
        return (updated, null);
    }

    public async Task<string?> DeactivateAsync(int customerId)
    {
        var response = await http.PostAsync($"api/v1/customers/{customerId}/deactivate", null).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? null : await ApiJson.ReadErrorAsync(response).ConfigureAwait(false);
    }
}
