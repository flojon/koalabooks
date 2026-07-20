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

// Covers the Accounts + FiscalYears endpoints added for issue #122 (Agent B stream):
// account create/update/toggle-active/copy-accounts/missing-from-source, and fiscal-year
// create/accounts-for-year/propagate-balances. Kept in its own file rather than growing
// ApiTests.cs further (already 1700+ lines) per the program plan's guidance.
public class AccountsAndFiscalYearsApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;
    private int _cashAccountId;

    private const string TestEmail = "api-test-accounts-fy@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _fiscalYearId, _cashAccountId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int fiscalYearId, int cashAccountId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Accounts/FY)", Slug = "api-test-accounts-fy", LegalForm = LegalForm.Aktiebolag };
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

        var cash = new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true, OutgoingBalance = 500m };
        db.Accounts.AddRange(cash,
            new Account { AccountNumber = "3000", Name = "Revenue", AccountClass = AccountClass.Revenue, FiscalYearId = fy.Id, IsActive = true }
        );
        await db.SaveChangesAsync();

        return (org.Id, fy.Id, cash.Id);
    }

    private async Task<(int orgId, int fiscalYearId, int accountId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-accounts-fy", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var fy2 = new FiscalYear
        {
            OrganisationId = org2.Id, Name = "2025",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31)
        };
        db.FiscalYears.Add(fy2);
        await db.SaveChangesAsync();

        var account2 = new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = fy2.Id, IsActive = true };
        db.Accounts.Add(account2);
        await db.SaveChangesAsync();

        return (org2.Id, fy2.Id, account2.Id);
    }

    /// <summary>Seeds a second fiscal year in the SAME tenant with one account not present in the primary year, for copy/missing-from-source tests.</summary>
    private async Task<(int fiscalYearId, int accountId)> SeedSameTenantSourceYearAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var fy = new FiscalYear
        {
            OrganisationId = _orgId, Name = "2024",
            StartDate = new DateOnly(2024, 1, 1), EndDate = new DateOnly(2024, 12, 31)
        };
        db.FiscalYears.Add(fy);
        await db.SaveChangesAsync();

        var account = new Account { AccountNumber = "1930", Name = "Bank", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        return (fy.Id, account.Id);
    }

    /// <summary>Seeds a "next" fiscal year linked via PreviousFiscalYearId, with a matching account, for propagate-balances tests.</summary>
    private async Task<(int fiscalYearId, int accountId)> SeedNextFiscalYearAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var fy = new FiscalYear
        {
            OrganisationId = _orgId, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31),
            PreviousFiscalYearId = _fiscalYearId
        };
        db.FiscalYears.Add(fy);
        await db.SaveChangesAsync();

        var account = new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true, IncomingBalance = 0m };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        return (fy.Id, account.Id);
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

    // ── Accounts: create ─────────────────────────────────────────────────────

    [Fact]
    public async Task Accounts_Create_ReturnsCreatedAccount()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts", new
        {
            accountNumber = "2440",
            name = "Accounts payable",
            accountClass = "Liability"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2440", json.GetProperty("accountNumber").GetString());
        Assert.Equal("Accounts payable", json.GetProperty("name").GetString());
        Assert.Equal("Liability", json.GetProperty("accountClass").GetString());
        Assert.True(json.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Accounts_Create_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/fiscal-years/999999/accounts", new
        {
            accountNumber = "2440",
            name = "Accounts payable",
            accountClass = "Liability"
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_Create_CrossTenantFiscalYear_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/accounts", new
        {
            accountNumber = "2440",
            name = "Accounts payable",
            accountClass = "Liability"
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_Create_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts", new
        {
            accountNumber = "2440",
            name = "Accounts payable",
            accountClass = "Liability"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Accounts: update ──────────────────────────────────────────────────────

    [Fact]
    public async Task Accounts_Update_UpdatesFields()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync($"/api/v1/accounts/{_cashAccountId}", new
        {
            accountNumber = "1911",
            name = "Petty cash",
            accountClass = "Asset"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1911", json.GetProperty("accountNumber").GetString());
        Assert.Equal("Petty cash", json.GetProperty("name").GetString());

        // Balances set outside the update request must be preserved, not zeroed out.
        Assert.Equal(500m, json.GetProperty("outgoingBalance").GetDecimal());
    }

    [Fact]
    public async Task Accounts_Update_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync("/api/v1/accounts/999999", new
        {
            accountNumber = "1911",
            name = "Petty cash",
            accountClass = "Asset"
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_Update_CrossTenant_Returns404()
    {
        var (_, _, otherAccountId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync($"/api/v1/accounts/{otherAccountId}", new
        {
            accountNumber = "1911",
            name = "Petty cash",
            accountClass = "Asset"
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_Update_WithoutToken_Returns401()
    {
        var response = await _client.PutAsJsonAsync($"/api/v1/accounts/{_cashAccountId}", new
        {
            accountNumber = "1911",
            name = "Petty cash",
            accountClass = "Asset"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Accounts: toggle-active ───────────────────────────────────────────────

    [Fact]
    public async Task Accounts_ToggleActive_TogglesFlag()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/v1/accounts/{_cashAccountId}/toggle-active", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("isActive").GetBoolean());

        var again = await client.PostAsync($"/api/v1/accounts/{_cashAccountId}/toggle-active", null);
        var json2 = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json2.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Accounts_ToggleActive_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/accounts/999999/toggle-active", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_ToggleActive_CrossTenant_Returns404()
    {
        var (_, _, otherAccountId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/v1/accounts/{otherAccountId}/toggle-active", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_ToggleActive_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync($"/api/v1/accounts/{_cashAccountId}/toggle-active", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Accounts: missing-from-source ────────────────────────────────────────

    [Fact]
    public async Task Accounts_MissingFromSource_ReturnsAccountsNotInTarget()
    {
        var (sourceYearId, sourceAccountId) = await SeedSameTenantSourceYearAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts/missing-from-source/{sourceYearId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(sourceAccountId, items[0].GetProperty("id").GetInt32());
        Assert.Equal("1930", items[0].GetProperty("accountNumber").GetString());
    }

    [Fact]
    public async Task Accounts_MissingFromSource_UnknownTargetFiscalYear_Returns404()
    {
        var (sourceYearId, _) = await SeedSameTenantSourceYearAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/999999/accounts/missing-from-source/{sourceYearId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_MissingFromSource_CrossTenantSourceYear_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts/missing-from-source/{otherFiscalYearId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_MissingFromSource_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts/missing-from-source/999999");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Accounts: copy-accounts ───────────────────────────────────────────────

    [Fact]
    public async Task Accounts_CopyAccounts_CopiesSelectedAccounts()
    {
        var (_, sourceAccountId) = await SeedSameTenantSourceYearAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/accounts/copy-accounts",
            new { accountIds = new[] { sourceAccountId } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());

        var listResponse = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(accounts.EnumerateArray(), a => a.GetProperty("accountNumber").GetString() == "1930");
    }

    [Fact]
    public async Task Accounts_CopyAccounts_UnknownTargetFiscalYear_Returns404()
    {
        var (_, sourceAccountId) = await SeedSameTenantSourceYearAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/fiscal-years/999999/accounts/copy-accounts",
            new { accountIds = new[] { sourceAccountId } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_CopyAccounts_CrossTenantAccountId_Returns404()
    {
        var (_, _, otherAccountId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/accounts/copy-accounts",
            new { accountIds = new[] { otherAccountId } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_CopyAccounts_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/accounts/copy-accounts",
            new { accountIds = new[] { 1 } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_CopyAccounts_NullAccountIds_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/accounts/copy-accounts",
            new { accountIds = (int[]?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── FiscalYears: create ───────────────────────────────────────────────────

    [Fact]
    public async Task FiscalYears_Create_ReturnsCreatedYear()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/fiscal-years", new
        {
            name = "2026",
            startDate = "2026-01-01",
            endDate = "2026-12-31"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2026", json.GetProperty("name").GetString());
        Assert.Equal("2026-01-01", json.GetProperty("startDate").GetString());
        Assert.False(json.GetProperty("isClosed").GetBoolean());
    }

    [Fact]
    public async Task FiscalYears_Create_OverlappingDates_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/fiscal-years", new
        {
            name = "2025-again",
            startDate = "2025-06-01",
            endDate = "2026-06-01"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FiscalYears_Create_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/fiscal-years", new
        {
            name = "2026",
            startDate = "2026-01-01",
            endDate = "2026-12-31"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── FiscalYears: accounts-for-year ────────────────────────────────────────

    [Fact]
    public async Task FiscalYears_AccountsForYear_ReturnsAccounts()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts-for-year");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, a => a.GetProperty("accountNumber").GetString() == "1910");
    }

    [Fact]
    public async Task FiscalYears_AccountsForYear_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/accounts-for-year");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FiscalYears_AccountsForYear_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts-for-year");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── FiscalYears: propagate-balances ───────────────────────────────────────

    [Fact]
    public async Task FiscalYears_PropagateBalances_UpdatesNextYearIncomingBalance()
    {
        var (nextYearId, _) = await SeedNextFiscalYearAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/v1/fiscal-years/{_fiscalYearId}/propagate-balances", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var nextAccounts = await client.GetAsync($"/api/v1/fiscal-years/{nextYearId}/accounts-for-year");
        var json = await nextAccounts.Content.ReadFromJsonAsync<JsonElement>();
        var cash = json.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910");
        Assert.Equal(500m, cash.GetProperty("incomingBalance").GetDecimal());
    }

    [Fact]
    public async Task FiscalYears_PropagateBalances_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/propagate-balances", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FiscalYears_PropagateBalances_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync($"/api/v1/fiscal-years/{_fiscalYearId}/propagate-balances", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
