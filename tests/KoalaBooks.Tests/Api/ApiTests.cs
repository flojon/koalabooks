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

    [Fact]
    public async Task ConnectToken_UnregisteredClientId_ReturnsUnauthorized()
    {
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("client_id", "not-a-real-client"),
            new KeyValuePair<string, string>("username", TestEmail),
            new KeyValuePair<string, string>("password", TestPassword)
        ]));
        // OpenIddict treats an unrecognized client_id as a client-authentication failure
        // (RFC 6749 §5.2 "invalid_client"), reported as 401.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConnectToken_MissingClientId_ReturnsUnauthorized()
    {
        // A request with no client_id at all used to be accepted anonymously; now that a
        // real client is registered and anonymous clients are no longer accepted, client_id
        // is mandatory and an omitted one must be rejected.
        var response = await _client.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("username", TestEmail),
            new KeyValuePair<string, string>("password", TestPassword)
        ]));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

    [Fact]
    public async Task FiscalYears_GetActive_ReturnsActiveYear()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/fiscal-years/active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_fiscalYearId, json.GetProperty("id").GetInt32());
        Assert.False(json.GetProperty("isClosed").GetBoolean());
    }

    [Fact]
    public async Task FiscalYears_GetActive_NoActiveYear_Returns404()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fy = await db.FiscalYears.IgnoreQueryFilters().FirstAsync(f => f.Id == _fiscalYearId);
            fy.IsClosed = true;
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/active");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FiscalYears_GetActive_CrossTenant_ReturnsOwnActiveYear()
    {
        // Seeds another org's fiscal year, which is active (not closed) by default — if the
        // tenant query filter didn't apply to GetActiveAsync, this could leak into the result.
        await SeedSecondTenantAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/active");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_fiscalYearId, json.GetProperty("id").GetInt32());
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
    public async Task JournalEntries_Update_CrossTenant_Returns404()
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
        var updateBody = new
        {
            date = "2025-01-16",
            description = "Hijacked",
            lines = new[] { new { accountId = otherAccountId, debitAmount = 200m, creditAmount = 0m } }
        };
        var response = await client.PutAsJsonAsync($"/api/v1/journal-entries/{otherEntry.Id}", updateBody);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Post_CrossTenant_Returns404()
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
        var response = await client.PostAsync($"/api/v1/journal-entries/{otherEntry.Id}/post", null);
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

    [Fact]
    public async Task JournalEntries_PreviewReversal_PostedEntry_ReturnsPreviewWithoutPersisting()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-09-03",
            description = "To be previewed",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 250m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 250m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        // Mark posted via raw DB write — see comment on JournalEntries_Reverse_PostedEntry_ReturnsReversalLinkedToOriginal
        // for why the service can't be called directly from a test-created scope.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entryToPost = await db.JournalEntries.IgnoreQueryFilters().FirstAsync(j => j.Id == entryId);
        entryToPost.IsPosted = true;
        entryToPost.Status = JournalEntryStatus.Posted;
        await db.SaveChangesAsync();

        var previewResp = await client.PostAsJsonAsync($"/api/v1/journal-entries/{entryId}/preview-reversal", new { reason = "Wrong account" });
        Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);

        var preview = await previewResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Correction", preview.GetProperty("status").GetString());
        Assert.Equal(entryId, preview.GetProperty("sourceJournalEntryId").GetInt32());
        var lines = preview.GetProperty("lines").EnumerateArray().ToList();
        var cashLine = lines.Single(l => l.GetProperty("accountId").GetInt32() == cashId);
        Assert.Equal(250m, cashLine.GetProperty("creditAmount").GetDecimal());
        Assert.Equal(0m, cashLine.GetProperty("debitAmount").GetDecimal());

        // Original entry must be untouched — preview must not persist anything.
        var originalResp = await client.GetAsync($"/api/v1/journal-entries/{entryId}");
        var original = await originalResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Posted", original.GetProperty("status").GetString());

        var listResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries");
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        var items = list.GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(items, i => i.GetProperty("sourceJournalEntryId").ValueKind != JsonValueKind.Null
            && i.GetProperty("sourceJournalEntryId").GetInt32() == entryId);
    }

    [Fact]
    public async Task JournalEntries_PreviewReversal_DraftEntry_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-09-04",
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

        var previewResp = await client.PostAsJsonAsync($"/api/v1/journal-entries/{entryId}/preview-reversal", new { reason = "Nope" });
        Assert.Equal(HttpStatusCode.BadRequest, previewResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_PreviewReversal_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/journal-entries/999999/preview-reversal", new { reason = "Nope" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_PreviewReversal_EmptyReason_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-09-05",
            description = "Posted, but empty reason",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 100m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entryToPost = await db.JournalEntries.IgnoreQueryFilters().FirstAsync(j => j.Id == entryId);
        entryToPost.IsPosted = true;
        entryToPost.Status = JournalEntryStatus.Posted;
        await db.SaveChangesAsync();

        var previewResp = await client.PostAsJsonAsync($"/api/v1/journal-entries/{entryId}/preview-reversal", new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, previewResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Update_DraftEntry_ReturnsUpdatedValues()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-01",
            description = "Original",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 300m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 300m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var updateBody = new
        {
            date = "2025-10-02",
            description = "Updated",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 400m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 400m }
            }
        };
        var updateResp = await client.PutAsJsonAsync($"/api/v1/journal-entries/{entryId}", updateBody);
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        var updated = await updateResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated", updated.GetProperty("description").GetString());
        Assert.Equal("2025-10-02", updated.GetProperty("date").GetString());
        Assert.Equal(400m, updated.GetProperty("lines")[0].GetProperty("debitAmount").GetDecimal());
    }

    [Fact]
    public async Task JournalEntries_Update_UnbalancedLines_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-03",
            description = "To be broken",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 100m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var updateBody = new
        {
            date = "2025-10-03",
            description = "Unbalanced now",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 50m }
            }
        };
        var updateResp = await client.PutAsJsonAsync($"/api/v1/journal-entries/{entryId}", updateBody);
        Assert.Equal(HttpStatusCode.BadRequest, updateResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Update_PostedEntry_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-07",
            description = "Already posted",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 150m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 150m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var postResp = await client.PostAsync($"/api/v1/journal-entries/{entryId}/post", null);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var updateBody = new
        {
            date = "2025-10-07",
            description = "Trying to edit a posted entry",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 150m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 150m }
            }
        };
        var updateResp = await client.PutAsJsonAsync($"/api/v1/journal-entries/{entryId}", updateBody);
        Assert.Equal(HttpStatusCode.BadRequest, updateResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Update_ClosedFiscalYear_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-08",
            description = "Before the year closes",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 200m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 200m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fy = await db.FiscalYears.IgnoreQueryFilters().FirstAsync(f => f.Id == _fiscalYearId);
            fy.IsClosed = true;
            await db.SaveChangesAsync();
        }

        var updateBody = new
        {
            date = "2025-10-08",
            description = "Trying to edit after close",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 200m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 200m }
            }
        };
        var updateResp = await client.PutAsJsonAsync($"/api/v1/journal-entries/{entryId}", updateBody);
        Assert.Equal(HttpStatusCode.BadRequest, updateResp.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Update_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var updateBody = new
        {
            date = "2025-10-04",
            description = "Nope",
            lines = new[] { new { accountId = 1, debitAmount = 10m, creditAmount = 0m } }
        };
        var response = await client.PutAsJsonAsync("/api/v1/journal-entries/999999", updateBody);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Post_DraftEntry_MarksPosted()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-05",
            description = "To be posted",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 700m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 700m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var postResp = await client.PostAsync($"/api/v1/journal-entries/{entryId}/post", null);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        var posted = await postResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(posted.GetProperty("isPosted").GetBoolean());
        Assert.Equal("Posted", posted.GetProperty("status").GetString());
    }

    [Fact]
    public async Task JournalEntries_Post_AlreadyPosted_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-06",
            description = "Double post",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 800m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 800m }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var firstPost = await client.PostAsync($"/api/v1/journal-entries/{entryId}/post", null);
        Assert.Equal(HttpStatusCode.OK, firstPost.StatusCode);

        var secondPost = await client.PostAsync($"/api/v1/journal-entries/{entryId}/post", null);
        Assert.Equal(HttpStatusCode.BadRequest, secondPost.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_Post_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/journal-entries/999999/post", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    [Fact]
    public async Task JournalEntries_DraftCount_ReturnsCountOfUnpostedEntries()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-01",
            description = "Draft entry",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 100m }
            }
        };
        await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries/draft-count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task JournalEntries_DraftCount_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/journal-entries/draft-count");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JournalEntries_DraftsForOrganisation_SpansAllOpenFiscalYears()
    {
        var client = await AuthenticatedClientAsync();

        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        var createBody = new
        {
            date = "2025-10-01",
            description = "Draft entry",
            lines = new[]
            {
                new { accountId = cashId, debitAmount = 100m, creditAmount = 0m },
                new { accountId = revenueId, debitAmount = 0m, creditAmount = 100m }
            }
        };
        await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);

        var countResponse = await client.GetAsync("/api/v1/journal-entries/draft-count");
        Assert.Equal(HttpStatusCode.OK, countResponse.StatusCode);
        var countJson = await countResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, countJson.GetProperty("count").GetInt32());

        var listResponse = await client.GetAsync("/api/v1/journal-entries/drafts");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, listJson.GetArrayLength());
    }

    // ── Supplier invoice tests ──────────────────────────────────────────────────

    [Fact]
    public async Task SupplierInvoices_List_ReturnsPaginatedResult()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("items", out _));
        Assert.True(json.TryGetProperty("totalCount", out _));
    }

    [Fact]
    public async Task SupplierInvoices_List_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/supplier-invoices");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_Create_ValidInvoice_Returns201WithLocation()
    {
        var client = await AuthenticatedClientAsync();

        var body = new
        {
            supplierName = "Acme AB",
            invoiceDate = "2026-03-01",
            dueDate = "2026-03-31",
            amountExclVat = 800m,
            vatAmount = 200m,
            totalAmount = 1000m
        };

        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme AB", json.GetProperty("supplierName").GetString());
        Assert.Equal(1000m, json.GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task SupplierInvoices_Create_ZeroTotal_Returns400()
    {
        var client = await AuthenticatedClientAsync();

        var body = new
        {
            supplierName = "Acme AB",
            invoiceDate = "2026-03-01",
            dueDate = "2026-03-31",
            amountExclVat = 0m,
            vatAmount = 0m,
            totalAmount = 0m
        };

        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_GetById_ReturnsInvoice()
    {
        var client = await AuthenticatedClientAsync();

        var createBody = new
        {
            supplierName = "Read-back Supplier",
            invoiceDate = "2026-04-01",
            dueDate = "2026-04-30",
            amountExclVat = 400m,
            vatAmount = 100m,
            totalAmount = 500m
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var invoiceId = created.GetProperty("id").GetInt32();

        var response = await client.GetAsync($"/api/v1/supplier-invoices/{invoiceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Read-back Supplier", json.GetProperty("supplierName").GetString());
    }

    [Fact]
    public async Task SupplierInvoices_GetById_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/supplier-invoices/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_Update_DraftInvoice_Returns200WithUpdatedFields()
    {
        var client = await AuthenticatedClientAsync();

        var createBody = new
        {
            supplierName = "Original Name",
            invoiceDate = "2026-05-01",
            dueDate = "2026-05-31",
            amountExclVat = 400m,
            vatAmount = 100m,
            totalAmount = 500m
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var invoiceId = created.GetProperty("id").GetInt32();

        var updateBody = new
        {
            supplierName = "Updated Name",
            invoiceDate = "2026-05-02",
            dueDate = "2026-06-01",
            amountExclVat = 450m,
            vatAmount = 112.5m,
            totalAmount = 562.5m
        };
        var updateResp = await client.PutAsJsonAsync($"/api/v1/supplier-invoices/{invoiceId}", updateBody);
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        var updated = await updateResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated Name", updated.GetProperty("supplierName").GetString());
    }

    [Fact]
    public async Task SupplierInvoices_Update_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var body = new
        {
            supplierName = "Nope",
            invoiceDate = "2026-05-01",
            dueDate = "2026-05-31",
            amountExclVat = 100m,
            vatAmount = 0m,
            totalAmount = 100m
        };
        var response = await client.PutAsJsonAsync("/api/v1/supplier-invoices/999999", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_Delete_DraftInvoice_Returns204()
    {
        var client = await AuthenticatedClientAsync();

        var createBody = new
        {
            supplierName = "To be deleted",
            invoiceDate = "2026-06-01",
            dueDate = "2026-06-30",
            amountExclVat = 100m,
            vatAmount = 25m,
            totalAmount = 125m
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var invoiceId = created.GetProperty("id").GetInt32();

        var deleteResp = await client.DeleteAsync($"/api/v1/supplier-invoices/{invoiceId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_Delete_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.DeleteAsync("/api/v1/supplier-invoices/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_GetById_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherInvoice = new SupplierInvoice
        {
            FiscalYearId = otherFiscalYearId,
            SupplierName = "Other tenant supplier",
            InvoiceDate = new DateOnly(2026, 1, 15),
            DueDate = new DateOnly(2026, 2, 15),
            AmountExclVat = 100m,
            VatAmount = 25m,
            TotalAmount = 125m
        };
        db.SupplierInvoices.Add(otherInvoice);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/supplier-invoices/{otherInvoice.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_UnpaidCount_ReturnsCountOfUnpaidInvoices()
    {
        var client = await AuthenticatedClientAsync();

        var createBody = new
        {
            supplierName = "Unpaid Supplier",
            invoiceDate = "2026-02-01",
            dueDate = "2026-02-28",
            amountExclVat = 400m,
            vatAmount = 100m,
            totalAmount = 500m
        };
        await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", createBody);

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices/unpaid-count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SupplierInvoices_UnpaidCount_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/supplier-invoices/unpaid-count");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SupplierInvoices_UnpaidCountForOrganisation_ReturnsCountOfUnpaidInvoices()
    {
        var client = await AuthenticatedClientAsync();

        var createBody = new
        {
            supplierName = "Unpaid Supplier",
            invoiceDate = "2026-02-01",
            dueDate = "2026-02-28",
            amountExclVat = 400m,
            vatAmount = 100m,
            totalAmount = 500m
        };
        await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", createBody);

        var response = await client.GetAsync("/api/v1/supplier-invoices/unpaid-count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    // ── Bank transaction tests ──────────────────────────────────────────────────

    [Fact]
    public async Task BankTransactions_List_ReturnsPaginatedResult()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cashAccount = await db.Accounts.IgnoreQueryFilters()
                .FirstAsync(a => a.FiscalYearId == _fiscalYearId && a.AccountNumber == "1910");
            db.BankTransactions.Add(new BankTransaction
            {
                OrganisationId = _orgId, AccountId = cashAccount.Id,
                Date = new DateOnly(2025, 6, 1), Amount = 500m, Description = "Deposit"
            });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/bank-transactions?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Deposit", items[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task BankTransactions_List_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/bank-transactions");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BankTransactions_List_FiltersByDateRange()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cashAccount = await db.Accounts.IgnoreQueryFilters()
                .FirstAsync(a => a.FiscalYearId == _fiscalYearId && a.AccountNumber == "1910");
            db.BankTransactions.AddRange(
                new BankTransaction { OrganisationId = _orgId, AccountId = cashAccount.Id, Date = new DateOnly(2025, 1, 1), Amount = 100m, Description = "January" },
                new BankTransaction { OrganisationId = _orgId, AccountId = cashAccount.Id, Date = new DateOnly(2025, 8, 1), Amount = 200m, Description = "August" });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/bank-transactions?from=2025-07-01&to=2025-12-31");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("August", items[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task BankTransactions_GetById_ReturnsTransaction()
    {
        int txId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cashAccount = await db.Accounts.IgnoreQueryFilters()
                .FirstAsync(a => a.FiscalYearId == _fiscalYearId && a.AccountNumber == "1910");
            var tx = new BankTransaction
            {
                OrganisationId = _orgId, AccountId = cashAccount.Id,
                Date = new DateOnly(2025, 5, 1), Amount = 300m, Description = "Read-back tx"
            };
            db.BankTransactions.Add(tx);
            await db.SaveChangesAsync();
            txId = tx.Id;
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/bank-transactions/{txId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Read-back tx", json.GetProperty("description").GetString());
    }

    [Fact]
    public async Task BankTransactions_GetById_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/bank-transactions/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BankTransactions_GetById_CrossTenant_Returns404()
    {
        var (otherOrgId, _, otherAccountId) = await SeedSecondTenantAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherTx = new BankTransaction
        {
            OrganisationId = otherOrgId, AccountId = otherAccountId,
            Date = new DateOnly(2025, 5, 1), Amount = 100m, Description = "Other tenant tx"
        };
        db.BankTransactions.Add(otherTx);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/bank-transactions/{otherTx.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BankTransactions_UnmatchedCount_ReturnsCountOfUnmatchedTransactions()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cashAccount = await db.Accounts.IgnoreQueryFilters()
                .FirstAsync(a => a.FiscalYearId == _fiscalYearId && a.AccountNumber == "1910");
            db.BankTransactions.Add(new BankTransaction
            {
                OrganisationId = _orgId, AccountId = cashAccount.Id,
                Date = new DateOnly(2025, 6, 1), Amount = 500m, Description = "Deposit"
            });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/bank-transactions/unmatched-count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task BankTransactions_UnmatchedCount_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/bank-transactions/unmatched-count");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BankTransactions_UnmatchedCountForOrganisation_ReturnsCountOfUnmatchedTransactions()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cashAccount = await db.Accounts.IgnoreQueryFilters()
                .FirstAsync(a => a.FiscalYearId == _fiscalYearId && a.AccountNumber == "1910");
            db.BankTransactions.Add(new BankTransaction
            {
                OrganisationId = _orgId, AccountId = cashAccount.Id,
                Date = new DateOnly(2025, 6, 1), Amount = 500m, Description = "Deposit"
            });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/bank-transactions/unmatched-count");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    // ── Report tests ─────────────────────────────────────────────────────────────

    private async Task<int> CreateAndPostEntryAsync(
        HttpClient client, DateOnly date, int debitAccountId, int creditAccountId, decimal amount, string description)
    {
        var createBody = new
        {
            date = date.ToString("yyyy-MM-dd"),
            description,
            lines = new[]
            {
                new { accountId = debitAccountId, debitAmount = amount, creditAmount = 0m },
                new { accountId = creditAccountId, debitAmount = 0m, creditAmount = amount }
            }
        };
        var createResp = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries", createBody);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var postResp = await client.PostAsync($"/api/v1/journal-entries/{entryId}/post", null);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);
        return entryId;
    }

    [Fact]
    public async Task Reports_DashboardStats_ReturnsEntryCountAndPostedTotals()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 1000m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/dashboard-stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("entryCount").GetInt32());
        Assert.Equal(1000m, json.GetProperty("totalDebit").GetDecimal());
        Assert.Equal(1000m, json.GetProperty("totalCredit").GetDecimal());
    }

    [Fact]
    public async Task Reports_DashboardStats_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/dashboard-stats");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_DashboardStats_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/reports/dashboard-stats");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_TrialBalance_ReturnsRowsForPostedEntries()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 1500m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/trial-balance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cashRow = json.EnumerateArray().First(r => r.GetProperty("accountNumber").GetString() == "1910");
        Assert.Equal(1500m, cashRow.GetProperty("totalDebit").GetDecimal());
        Assert.Equal(1500m, cashRow.GetProperty("balance").GetDecimal());
        Assert.Equal("Asset", cashRow.GetProperty("accountClass").GetString());
    }

    [Fact]
    public async Task Reports_TrialBalance_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/trial-balance");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_BalanceSheet_GroupsAccountsUnderAssetLiabilityEquitySections()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 2000m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/balance-sheet");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var assets = json.EnumerateArray().First(s => s.GetProperty("title").GetString() == "Tillgångar");
        var cashRow = assets.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("accountNumber").GetString() == "1910");
        Assert.Equal(2000m, cashRow.GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Reports_BalanceSheet_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/balance-sheet");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_IncomeStatement_ReturnsSectionsAndNetResult()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 2500m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/income-statement");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2500m, json.GetProperty("netResult").GetDecimal());
        var revenueSection = json.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("title").GetString() == "Intäkter");
        Assert.Equal(2500m, revenueSection.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Reports_IncomeStatement_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/income-statement");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_VatReport_ReturnsEmptySectionsWhenNoVatAccounts()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/vat-report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("outputVat").GetProperty("rows").GetArrayLength());
        Assert.Equal(0m, json.GetProperty("netPayable").GetDecimal());
    }

    [Fact]
    public async Task Reports_VatReport_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/vat-report");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_GeneralLedger_ReturnsAccountSectionsWithRunningBalance()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 900m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/general-ledger");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cashSection = json.EnumerateArray().First(s => s.GetProperty("accountNumber").GetString() == "1910");
        Assert.Equal(900m, cashSection.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(1, cashSection.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Reports_GeneralLedger_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/general-ledger");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_AccountLedger_ReturnsSingleAccountSection()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 1200m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/general-ledger/accounts/{cashId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1910", json.GetProperty("accountNumber").GetString());
        Assert.Equal(1200m, json.GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Reports_AccountLedger_UnknownAccount_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/general-ledger/accounts/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_AccountLedger_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();

        var response = await client.GetAsync($"/api/v1/fiscal-years/999999/reports/general-ledger/accounts/{cashId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_ComputedBalances_ReturnsIncomingAndClosingPerAccount()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 600m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/general-ledger/computed-balances");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var cashBalance = json.EnumerateArray().First(b => b.GetProperty("accountId").GetInt32() == cashId);
        Assert.Equal(600m, cashBalance.GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Reports_ComputedBalances_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/general-ledger/computed-balances");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_AccountIdsWithTransactions_ReturnsOnlyAccountsWithPostedActivity()
    {
        var client = await AuthenticatedClientAsync();
        var accountsResp = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/accounts");
        var accounts = await accountsResp.Content.ReadFromJsonAsync<JsonElement>();
        var cashId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "1910").GetProperty("id").GetInt32();
        var revenueId = accounts.EnumerateArray().First(a => a.GetProperty("accountNumber").GetString() == "3000").GetProperty("id").GetInt32();

        await CreateAndPostEntryAsync(client, new DateOnly(2025, 3, 1), cashId, revenueId, 300m, "Sale");

        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/general-ledger/account-ids-with-transactions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = json.EnumerateArray().Select(e => e.GetInt32()).ToList();
        Assert.Contains(cashId, ids);
        Assert.Contains(revenueId, ids);
    }

    [Fact]
    public async Task Reports_AccountIdsWithTransactions_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/reports/general-ledger/account-ids-with-transactions");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reports_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/reports/dashboard-stats");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
