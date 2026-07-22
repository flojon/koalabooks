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

// Covers the AccountMappingController endpoints added for issue #122 (Agent H stream):
// build-mapping and apply-mapping between a source and target fiscal year. Kept in its own
// file per the program plan's guidance (each new controller gets its own test file).
public class AccountMappingApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _sourceFiscalYearId;
    private int _targetFiscalYearId;

    private const string TestEmail = "api-test-account-mapping@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _sourceFiscalYearId, _targetFiscalYearId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int sourceFiscalYearId, int targetFiscalYearId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Account Mapping)", Slug = "api-test-account-mapping", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        var sourceFy = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2024", IsClosed = true,
            StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2024, 12, 31)
        };
        var targetFy = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2025",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31)
        };
        db.FiscalYears.AddRange(sourceFy, targetFy);
        await db.SaveChangesAsync();

        db.Accounts.AddRange(
            new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = sourceFy.Id, IsActive = true, OutgoingBalance = 1000m },
            new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = targetFy.Id, IsActive = true }
        );
        await db.SaveChangesAsync();

        return (org.Id, sourceFy.Id, targetFy.Id);
    }

    private async Task<(int orgId, int fiscalYearId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-account-mapping", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var fy2 = new FiscalYear
        {
            OrganisationId = org2.Id, Name = "2025",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31)
        };
        db.FiscalYears.Add(fy2);
        await db.SaveChangesAsync();

        return (org2.Id, fy2.Id);
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

    // ── GET build-mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task BuildMapping_ReturnsMappingRows()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync(
            $"/api/v1/fiscal-years/{_sourceFiscalYearId}/account-mapping/{_targetFiscalYearId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetArrayLength());
        var row = json[0];
        Assert.Equal("1910", row.GetProperty("sourceAccountNumber").GetString());
        Assert.Equal(1000m, row.GetProperty("ub").GetDecimal());
        Assert.Equal("1910", row.GetProperty("targetAccountNumber").GetString());
    }

    [Fact]
    public async Task BuildMapping_UnknownSourceFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/999999/account-mapping/{_targetFiscalYearId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BuildMapping_CrossTenantTargetFiscalYear_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_sourceFiscalYearId}/account-mapping/{otherFiscalYearId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BuildMapping_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync(
            $"/api/v1/fiscal-years/{_sourceFiscalYearId}/account-mapping/{_targetFiscalYearId}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST apply-mapping ────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyMapping_AppliesRowsAndReturnsCounts()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_sourceFiscalYearId}/account-mapping/{_targetFiscalYearId}/apply",
            new
            {
                rows = new[]
                {
                    new { sourceAccountNumber = "1910", sourceAccountName = "Cash", ub = 1000m, targetAccountNumber = "1910" }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("mapped").GetInt32());
        Assert.Equal(0, json.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task ApplyMapping_UnknownTargetFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_sourceFiscalYearId}/account-mapping/999999/apply",
            new { rows = new[] { new { sourceAccountNumber = "1910", sourceAccountName = "Cash", ub = 1000m, targetAccountNumber = (string?)"1910" } } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApplyMapping_CrossTenantSourceFiscalYear_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{otherFiscalYearId}/account-mapping/{_targetFiscalYearId}/apply",
            new { rows = new[] { new { sourceAccountNumber = "1910", sourceAccountName = "Cash", ub = 1000m, targetAccountNumber = (string?)"1910" } } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApplyMapping_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_sourceFiscalYearId}/account-mapping/{_targetFiscalYearId}/apply",
            new { rows = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
