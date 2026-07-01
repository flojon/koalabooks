using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

    private async Task<(int orgId, int fiscalYearId, int accountId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org", LegalForm = LegalForm.Aktiebolag };
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

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var token = await GetBearerTokenAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string? DecodeOrgIdFromToken(string token)
    {
        var payload = token.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        var claims = JsonSerializer.Deserialize<JsonElement>(json);
        return claims.TryGetProperty("org_id", out var v) ? v.GetString() : null;
    }

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
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/fiscal-years");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("2025", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task FiscalYears_GetById_ReturnsCorrectYear()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_fiscalYearId, json.GetProperty("id").GetInt32());
        Assert.Equal("2025", json.GetProperty("name").GetString());
        Assert.False(json.GetProperty("isClosed").GetBoolean());
    }

    [Fact]
    public async Task FiscalYears_GetById_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FiscalYears_CrossTenant_Returns404()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var fy2 = new FiscalYear
        {
            OrganisationId = org2.Id, Name = "2025",
            StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31)
        };
        db.FiscalYears.Add(fy2);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{fy2.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Account tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accounts_GetByFiscalYear_ReturnsSeededAccounts()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, a => a.GetProperty("accountNumber").GetString() == "1910");
        Assert.Contains(items, a => a.GetProperty("accountNumber").GetString() == "3000");
    }

    [Fact]
    public async Task Accounts_GetByFiscalYear_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/accounts");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_GetById_ReturnsCorrectAccount()
    {
        var client = await AuthenticatedClientAsync();

        var listResponse = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var cashAccount = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910");
        var accountId = cashAccount.GetProperty("id").GetInt32();

        var response = await client.GetAsync($"/api/v1/accounts/{accountId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1910", json.GetProperty("accountNumber").GetString());
        Assert.Equal("Asset", json.GetProperty("accountClass").GetString());
    }

    [Fact]
    public async Task Accounts_GetById_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/accounts/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_GetById_CrossTenant_Returns404()
    {
        var (_, _, otherAccountId) = await SeedSecondTenantAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/accounts/{otherAccountId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Journal entry tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task JournalEntries_List_ReturnsPaginatedResult()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("items", out _));
        Assert.True(json.TryGetProperty("totalCount", out _));
        Assert.True(json.TryGetProperty("page", out var page));
        Assert.Equal(1, page.GetInt32());
    }

    [Fact]
    public async Task JournalEntries_List_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/journal-entries");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Create_ValidEntry_Returns201WithLocation()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var body = new
        {
            date = "2025-06-01",
            description = "Test entry",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 1000m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 1000m }
            }
        };

        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Test entry", json.GetProperty("description").GetString());
        Assert.Equal(2, json.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task JournalEntries_Create_UnbalancedLines_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var body = new
        {
            date = "2025-06-01",
            description = "Unbalanced",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 1000m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 500m }
            }
        };

        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Delete_DraftEntry_Returns204()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-07-01",
            description = "To be deleted",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 500m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 500m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var deleteResp = await client.DeleteAsync($"/api/v1/journal-entries/{entryId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Delete_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.DeleteAsync("/api/v1/journal-entries/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_GetById_ReturnsEntryWithLines()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-08-01",
            description = "Read-back test",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 200m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 200m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var response = await client.GetAsync($"/api/v1/journal-entries/{entryId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Read-back test", json.GetProperty("description").GetString());
        Assert.Equal(2, json.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task JournalEntries_GetById_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, otherAccountId) = await SeedSecondTenantAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherEntry = new JournalEntry
        {
            FiscalYearId = otherFiscalYearId,
            Date = new DateOnly(2025, 1, 15),
            Description = "Other tenant entry",
            Lines =
            [
                new JournalEntryLine { AccountId = otherAccountId, DebitAmount = 100, CreditAmount = 0 },
                new JournalEntryLine { AccountId = otherAccountId, DebitAmount = 0, CreditAmount = 100 }
            ]
        };
        db.JournalEntries.Add(otherEntry);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/journal-entries/{otherEntry.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Reverse_PostedEntry_ReturnsReversalLinkedToOriginal()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-09-01",
            description = "To be reversed",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 600m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 600m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        // Posting goes through JournalEntryService.PostAsync, but that (and every other service
        // method) relies on ICurrentUser.OrganisationId, which is sourced from the HTTP request's
        // claims. A manually created IServiceScope has no ambient HttpContext, so calling the
        // service directly here would silently fail the tenant query filter. Mark the entry
        // posted via a raw DB write instead, matching the SeedAsync/SeedSecondTenantAsync pattern
        // already used in this file for out-of-band setup.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entryToPost = await db.JournalEntries.IgnoreQueryFilters().FirstAsync(j => j.Id == entryId);
        entryToPost.IsPosted = true;
        entryToPost.Status = JournalEntryStatus.Posted;
        await db.SaveChangesAsync();

        var reverseResp = await client.PostAsJsonAsync($"/api/v1/journal-entries/{entryId}/reverse", new { reason = "Wrong account" });
        Assert.Equal(HttpStatusCode.Created, reverseResp.StatusCode);

        var reversal = await reverseResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Correction", reversal.GetProperty("status").GetString());
        Assert.Equal(entryId, reversal.GetProperty("sourceJournalEntryId").GetInt32());

        var originalResp = await client.GetAsync($"/api/v1/journal-entries/{entryId}");
        var original = await originalResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Reversed", original.GetProperty("status").GetString());
    }

    [Fact]
    public async Task JournalEntries_Reverse_DraftEntry_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-09-02",
            description = "Still a draft",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 100m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var reverseResp = await client.PostAsJsonAsync($"/api/v1/journal-entries/{entryId}/reverse", new { reason = "Nope" });
        Assert.Equal(HttpStatusCode.BadRequest, reverseResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Reverse_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/journal-entries/999999/reverse", new { reason = "Nope" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
