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

// Covers the VoucherGapsController endpoints added for issue #122 (Agent H stream): gaps,
// unexplained gaps, explanations list, and add-explanation. Kept in its own file per the
// program plan's guidance.
public class VoucherGapsApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;

    private const string TestEmail = "api-test-voucher-gaps@koalabooks.test";
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

        var org = new Organisation { Name = "API Test Org (Voucher Gaps)", Slug = "api-test-voucher-gaps", LegalForm = LegalForm.Aktiebolag };
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

        var cash = new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true };
        var revenue = new Account { AccountNumber = "3000", Name = "Revenue", AccountClass = AccountClass.Revenue, FiscalYearId = fy.Id, IsActive = true };
        db.Accounts.AddRange(cash, revenue);
        await db.SaveChangesAsync();

        // Entry numbers 1 and 3 exist, 2 is a gap.
        db.JournalEntries.AddRange(
            new JournalEntry
            {
                EntryNumber = 1, FiscalYearId = fy.Id, Date = new DateOnly(2025, 1, 5), Description = "Entry 1",
                IsPosted = true, Status = JournalEntryStatus.Posted,
                Lines = [
                    new JournalEntryLine { AccountId = cash.Id, DebitAmount = 100, CreditAmount = 0 },
                    new JournalEntryLine { AccountId = revenue.Id, DebitAmount = 0, CreditAmount = 100 }
                ]
            },
            new JournalEntry
            {
                EntryNumber = 3, FiscalYearId = fy.Id, Date = new DateOnly(2025, 1, 6), Description = "Entry 3",
                IsPosted = true, Status = JournalEntryStatus.Posted,
                Lines = [
                    new JournalEntryLine { AccountId = cash.Id, DebitAmount = 50, CreditAmount = 0 },
                    new JournalEntryLine { AccountId = revenue.Id, DebitAmount = 0, CreditAmount = 50 }
                ]
            }
        );
        await db.SaveChangesAsync();

        return (org.Id, fy.Id);
    }

    private async Task<(int orgId, int fiscalYearId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-voucher-gaps", LegalForm = LegalForm.Aktiebolag };
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

    // ── GET gaps ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGaps_ReturnsGapNumbers()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetArrayLength());
        Assert.Equal(2, json[0].GetInt32());
    }

    [Fact]
    public async Task GetGaps_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/voucher-gaps");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGaps_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/voucher-gaps");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetGaps_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── GET unexplained ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetUnexplainedGaps_ReturnsAllGapsBeforeExplained()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps/unexplained");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetArrayLength());
        Assert.Equal(2, json[0].GetInt32());
    }

    [Fact]
    public async Task GetUnexplainedGaps_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/voucher-gaps/unexplained");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST/GET explanations ─────────────────────────────────────────────────

    [Fact]
    public async Task AddExplanation_ThenGetExplanations_ReturnsIt()
    {
        var client = await AuthenticatedClientAsync();
        var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps/explanations",
            new { missingEntryNumber = 2, explanation = "Voided, printed wrong", explainedBy = "api-tester" });

        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var addJson = await addResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, addJson.GetArrayLength());
        Assert.Equal(2, addJson[0].GetProperty("missingEntryNumber").GetInt32());
        Assert.Equal("Voided, printed wrong", addJson[0].GetProperty("explanation").GetString());

        // Now the gap is explained, so unexplained should be empty.
        var unexplainedResponse = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps/unexplained");
        var unexplainedJson = await unexplainedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, unexplainedJson.GetArrayLength());

        var listResponse = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps/explanations");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, listJson.GetArrayLength());
    }

    [Fact]
    public async Task AddExplanation_NotAGap_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps/explanations",
            new { missingEntryNumber = 1, explanation = "Not actually a gap", explainedBy = "api-tester" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddExplanation_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            "/api/v1/fiscal-years/999999/voucher-gaps/explanations",
            new { missingEntryNumber = 2, explanation = "x", explainedBy = "api-tester" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddExplanation_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{otherFiscalYearId}/voucher-gaps/explanations",
            new { missingEntryNumber = 2, explanation = "x", explainedBy = "api-tester" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddExplanation_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/voucher-gaps/explanations",
            new { missingEntryNumber = 2, explanation = "x", explainedBy = "api-tester" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
