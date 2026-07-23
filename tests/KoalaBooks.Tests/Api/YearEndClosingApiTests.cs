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

// Covers the YearEndClosingController endpoints added for issue #122 (Agent H stream):
// the validate/preview/execute triad nested under fiscal-years/{id}/year-end-closing, per
// the program plan's 5.B resolution. Kept in its own file per the program plan's guidance.
public class YearEndClosingApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _closableFiscalYearId;
    private int _notClosableFiscalYearId;

    private const string TestEmail = "api-test-year-end-closing@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _closableFiscalYearId, _notClosableFiscalYearId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int closableFiscalYearId, int notClosableFiscalYearId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Year-End Closing)", Slug = "api-test-year-end-closing", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        // A fiscal year that ended in the past, fully posted, no gaps — ready to close.
        var closableFy = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2023",
            StartDate = new DateOnly(2023, 1, 1), EndDate = new DateOnly(2023, 12, 31)
        };
        // A fiscal year that hasn't ended yet — not closable.
        var notClosableFy = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2099",
            StartDate = new DateOnly(2099, 1, 1), EndDate = new DateOnly(2099, 12, 31)
        };
        db.FiscalYears.AddRange(closableFy, notClosableFy);
        await db.SaveChangesAsync();

        var cash = new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = closableFy.Id, IsActive = true };
        var revenue = new Account { AccountNumber = "3000", Name = "Revenue", AccountClass = AccountClass.Revenue, FiscalYearId = closableFy.Id, IsActive = true };
        db.Accounts.AddRange(cash, revenue);
        await db.SaveChangesAsync();

        db.JournalEntries.Add(new JournalEntry
        {
            EntryNumber = 1, FiscalYearId = closableFy.Id, Date = new DateOnly(2023, 6, 1), Description = "Sale",
            IsPosted = true, Status = JournalEntryStatus.Posted,
            Lines = [
                new JournalEntryLine { AccountId = cash.Id, DebitAmount = 1000, CreditAmount = 0 },
                new JournalEntryLine { AccountId = revenue.Id, DebitAmount = 0, CreditAmount = 1000 }
            ]
        });
        await db.SaveChangesAsync();

        return (org.Id, closableFy.Id, notClosableFy.Id);
    }

    private async Task<(int orgId, int fiscalYearId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-year-end-closing", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var fy2 = new FiscalYear
        {
            OrganisationId = org2.Id, Name = "2023",
            StartDate = new DateOnly(2023, 1, 1), EndDate = new DateOnly(2023, 12, 31)
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

    // ── GET validate ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_ReadyFiscalYear_ReturnsValid()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_closableFiscalYearId}/year-end-closing/validate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isValid").GetBoolean());
        Assert.Equal(0, json.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task Validate_NotYetEndedFiscalYear_ReturnsInvalidWithErrors()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_notClosableFiscalYearId}/year-end-closing/validate");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("isValid").GetBoolean());
        Assert.True(json.GetProperty("errors").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Validate_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/year-end-closing/validate");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Validate_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/year-end-closing/validate");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Validate_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_closableFiscalYearId}/year-end-closing/validate");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET preview ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ReadyFiscalYear_ReturnsClosingEntries()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_closableFiscalYearId}/year-end-closing/preview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isValid").GetBoolean());
        Assert.Equal(1000m, json.GetProperty("totalRevenue").GetDecimal());
        Assert.Equal(1000m, json.GetProperty("netResult").GetDecimal());
        Assert.True(json.GetProperty("entries").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Preview_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/year-end-closing/preview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Preview_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/year-end-closing/preview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Preview_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_closableFiscalYearId}/year-end-closing/preview");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST execute ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_ReadyFiscalYear_ClosesAndReturnsEntryNumbers()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/fiscal-years/{_closableFiscalYearId}/year-end-closing/execute", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("closingEntry1Number").ValueKind);
    }

    [Fact]
    public async Task Execute_NotYetEndedFiscalYear_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/fiscal-years/{_notClosableFiscalYearId}/year-end-closing/execute", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Execute_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/fiscal-years/999999/year-end-closing/execute", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Execute_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/year-end-closing/execute", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Execute_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync($"/api/v1/fiscal-years/{_closableFiscalYearId}/year-end-closing/execute", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
