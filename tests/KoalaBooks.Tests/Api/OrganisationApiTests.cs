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

// Covers the OrganisationsController endpoints: GET current organisation and PUT update.
public class OrganisationApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;

    private const string TestEmail = "api-test-organisation@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        _orgId = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<int> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation
        {
            Name = "API Test Org (Organisation)", Slug = "api-test-organisation",
            OrgNumber = "556677-8899", LegalForm = LegalForm.Aktiebolag
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        return org.Id;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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

    // ── GET current ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrent_ReturnsOrganisation()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/organisation");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_orgId, json.GetProperty("id").GetInt32());
        Assert.Equal("API Test Org (Organisation)", json.GetProperty("name").GetString());
        Assert.Equal("556677-8899", json.GetProperty("orgNumber").GetString());
        Assert.Equal("Aktiebolag", json.GetProperty("legalForm").GetString());
    }

    [Fact]
    public async Task GetCurrent_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/organisation");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── PUT update ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ValidRequest_UpdatesAndReturns204()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync("/api/v1/organisation",
            new { name = "Updated Org Name", orgNumber = "112233-4455" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync("/api/v1/organisation");
        var json = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated Org Name", json.GetProperty("name").GetString());
        Assert.Equal("112233-4455", json.GetProperty("orgNumber").GetString());
    }

    [Fact]
    public async Task Update_BlankName_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync("/api/v1/organisation",
            new { name = "   ", orgNumber = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutToken_Returns401()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/organisation",
            new { name = "Some Name", orgNumber = (string?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
