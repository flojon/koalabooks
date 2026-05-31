using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace KoalaBooks.Tests;

public class ApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;

    private const string TestEmail = "api-test@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _fiscalYearId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int fiscalYearId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org", Slug = "api-test", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        var fy = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2025",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31)
        };
        db.FiscalYears.Add(fy);
        await db.SaveChangesAsync();

        db.Accounts.AddRange(
            new Account { AccountNumber = "1910", Name = "Cash",    AccountClass = AccountClass.Asset,   FiscalYearId = fy.Id, IsActive = true },
            new Account { AccountNumber = "3000", Name = "Revenue", AccountClass = AccountClass.Revenue, FiscalYearId = fy.Id, IsActive = true }
        );
        await db.SaveChangesAsync();

        return (org.Id, fy.Id);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<string> GetBearerTokenAsync()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("username", TestEmail),
            new KeyValuePair<string, string>("password", TestPassword)
        ]));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    private static string? DecodeOrgIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        var claims = JsonSerializer.Deserialize<JsonElement>(json);
        return claims.TryGetProperty("org_id", out var v) ? v.GetString() : null;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ── Auth tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FiscalYears_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/fiscal-years");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConnectToken_ValidCredentials_ReturnsAccessTokenWithOrgId()
    {
        var token = await GetBearerTokenAsync();
        Assert.NotEmpty(token);
        var orgId = DecodeOrgIdFromToken(token);
        Assert.Equal(_orgId.ToString(), orgId);
    }

    // ── Fiscal year tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task FiscalYears_ReturnsOnlyTenantFiscalYears()
    {
        var token = await GetBearerTokenAsync();
        UseToken(token);

        var response = await _client.GetAsync("/api/v1/fiscal-years");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("2025", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task FiscalYears_GetById_ReturnsCorrectYear()
    {
        var token = await GetBearerTokenAsync();
        UseToken(token);

        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_fiscalYearId, json.GetProperty("id").GetInt32());
        Assert.Equal("2025", json.GetProperty("name").GetString());
        Assert.False(json.GetProperty("isClosed").GetBoolean());
    }

    [Fact]
    public async Task FiscalYears_GetById_UnknownId_Returns404()
    {
        var token = await GetBearerTokenAsync();
        UseToken(token);

        var response = await _client.GetAsync("/api/v1/fiscal-years/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
