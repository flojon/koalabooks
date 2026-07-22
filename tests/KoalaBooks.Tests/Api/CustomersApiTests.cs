using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace KoalaBooks.Tests;

// Covers CustomersController, added for issue #122's "Agent E" stream (folded into #290).
public class CustomersApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _customerId;

    private const string TestEmail = "api-test-customers@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _customerId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int customerId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Customers)", Slug = "api-test-customers", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        var customer = new Customer { OrganisationId = org.Id, Name = "Acme AB" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return (org.Id, customer.Id);
    }

    private async Task<int> SeedOtherTenantCustomerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-customers", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var customer = new Customer { OrganisationId = org2.Id, Name = "Other Customer" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return customer.Id;
    }

    private async Task<string> GetBearerTokenAsync()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", "koalabooks-api"),
            new KeyValuePair<string, string>("username", TestEmail),
            new KeyValuePair<string, string>("password", TestPassword)
        ]));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var token = await GetBearerTokenAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetAll_ReturnsCustomersForCurrentOrg()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Acme AB", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetAll_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_ReturnsCustomer()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customers/{_customerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme AB", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/customers/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_CrossTenant_Returns404()
    {
        var otherCustomerId = await SeedOtherTenantCustomerAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customers/{otherCustomerId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsCreatedCustomer()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { name = "New Customer AB", country = "SE" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New Customer AB", json.GetProperty("name").GetString());
        Assert.True(json.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Create_MissingName_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/customers", new { name = "New Customer AB" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_UpdatesFields()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync($"/api/v1/customers/{_customerId}", new { name = "Acme Renamed AB", email = "info@acme.example" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme Renamed AB", json.GetProperty("name").GetString());
        Assert.Equal("info@acme.example", json.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync("/api/v1/customers/999999", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_CrossTenant_Returns404()
    {
        var otherCustomerId = await SeedOtherTenantCustomerAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync($"/api/v1/customers/{otherCustomerId}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutToken_Returns401()
    {
        var response = await _client.PutAsJsonAsync($"/api/v1/customers/{_customerId}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsIsActiveFalse()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/customers/{_customerId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var listResponse = await client.GetAsync("/api/v1/customers");
        var json = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.EnumerateArray()); // GetAllAsync only returns active customers
    }

    [Fact]
    public async Task Deactivate_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/customers/999999/deactivate", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_CrossTenant_Returns404()
    {
        var otherCustomerId = await SeedOtherTenantCustomerAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/customers/{otherCustomerId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync($"/api/v1/customers/{_customerId}/deactivate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
