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

// Covers the bank transaction/import endpoints added for issue #122 (Agent D stream):
// unmatched, parse-preview, import, suggest-contra, set-status, match-to-entry. Kept in its
// own file rather than growing ApiTests.cs further (already 1800+ lines) per the program
// plan's guidance. The existing list/by-id/unmatched-count endpoints (PR #272) stay tested
// in ApiTests.cs — not touched here.
public class BankTransactionsApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;
    private int _cashAccountId;
    private int _revenueAccountId;
    private int _contraAccountId;

    private const string TestEmail = "api-test-bank-tx@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _fiscalYearId, _cashAccountId, _revenueAccountId, _contraAccountId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int fiscalYearId, int cashAccountId, int revenueAccountId, int contraAccountId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Bank)", Slug = "api-test-bank-tx", LegalForm = LegalForm.Aktiebolag };
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
        // Legal-form default contra account for Aktiebolag (see BankImportService.GetLegalFormDefaultAsync).
        var contra = new Account { AccountNumber = "2893", Name = "Skulder till koncernföretag", AccountClass = AccountClass.Liability, FiscalYearId = fy.Id, IsActive = true };
        db.Accounts.AddRange(cash, revenue, contra);
        await db.SaveChangesAsync();

        return (org.Id, fy.Id, cash.Id, revenue.Id, contra.Id);
    }

    private async Task<(int orgId, int fiscalYearId, int accountId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-bank-tx", LegalForm = LegalForm.Aktiebolag };
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

    private static MultipartFormDataContent BuildParsePreviewForm(
        string csv, string fileName = "transactions.csv",
        int dateCol = 0, int amountCol = 1, int descCol = 2, int? refCol = null, string dateFormat = "yyyy-MM-dd")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "File", fileName);
        content.Add(new StringContent(dateCol.ToString()), "DateCol");
        content.Add(new StringContent(amountCol.ToString()), "AmountCol");
        content.Add(new StringContent(descCol.ToString()), "DescCol");
        if (refCol.HasValue) content.Add(new StringContent(refCol.Value.ToString()), "RefCol");
        content.Add(new StringContent(dateFormat), "DateFormat");
        return content;
    }

    // ── GET unmatched ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unmatched_ReturnsOnlyUnmatchedTransactions()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BankTransactions.AddRange(
                new BankTransaction { OrganisationId = _orgId, AccountId = _cashAccountId, Date = new DateOnly(2025, 6, 1), Amount = 500m, Description = "Unmatched one", Status = BankTransactionStatus.Unmatched },
                new BankTransaction { OrganisationId = _orgId, AccountId = _cashAccountId, Date = new DateOnly(2025, 6, 2), Amount = 200m, Description = "Ignored one", Status = BankTransactionStatus.Ignored });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/bank-transactions/unmatched");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Unmatched one", items[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Unmatched_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/bank-transactions/unmatched");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unmatched_CrossTenantFiscalYear_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/bank-transactions/unmatched");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unmatched_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/bank-transactions/unmatched");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST parse-preview ──────────────────────────────────────────────────

    [Fact]
    public async Task ParsePreview_ReturnsPreviewRows()
    {
        var client = await AuthenticatedClientAsync();
        var csv = "Datum;Belopp;Beskrivning\n2025-06-01;500,00;Deposit\n";

        var response = await client.PostAsync(
            $"/api/v1/accounts/{_cashAccountId}/bank-transactions/parse-preview", BuildParsePreviewForm(csv));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        var previews = json.GetProperty("previews").EnumerateArray().ToList();
        Assert.Single(previews);
        Assert.Equal("Deposit", previews[0].GetProperty("description").GetString());
        Assert.Equal(500.00m, previews[0].GetProperty("amount").GetDecimal());
        Assert.False(previews[0].GetProperty("isDuplicate").GetBoolean());
    }

    [Fact]
    public async Task ParsePreview_MarksExistingTransactionAsDuplicate()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BankTransactions.Add(new BankTransaction
            {
                OrganisationId = _orgId, AccountId = _cashAccountId,
                Date = new DateOnly(2025, 6, 1), Amount = 500.00m, Description = "Deposit"
            });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var csv = "Datum;Belopp;Beskrivning\n2025-06-01;500,00;Deposit\n";

        var response = await client.PostAsync(
            $"/api/v1/accounts/{_cashAccountId}/bank-transactions/parse-preview", BuildParsePreviewForm(csv));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var previews = json.GetProperty("previews").EnumerateArray().ToList();
        Assert.Single(previews);
        Assert.True(previews[0].GetProperty("isDuplicate").GetBoolean());
    }

    [Fact]
    public async Task ParsePreview_UnsupportedFormat_ReturnsFailureEnvelope()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync(
            $"/api/v1/accounts/{_cashAccountId}/bank-transactions/parse-preview",
            BuildParsePreviewForm("irrelevant content", fileName: "transactions.doc"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task ParsePreview_UnknownAccount_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync(
            "/api/v1/accounts/999999/bank-transactions/parse-preview", BuildParsePreviewForm("a;b;c\n1;2;3\n"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ParsePreview_CrossTenantAccount_Returns404()
    {
        var (_, _, otherAccountId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsync(
            $"/api/v1/accounts/{otherAccountId}/bank-transactions/parse-preview", BuildParsePreviewForm("a;b;c\n1;2;3\n"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ParsePreview_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync(
            $"/api/v1/accounts/{_cashAccountId}/bank-transactions/parse-preview", BuildParsePreviewForm("a;b;c\n1;2;3\n"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ParsePreview_MissingFile_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var content = new MultipartFormDataContent
        {
            { new StringContent("0"), "DateCol" },
            { new StringContent("1"), "AmountCol" },
            { new StringContent("2"), "DescCol" },
            { new StringContent("yyyy-MM-dd"), "DateFormat" }
        };

        var response = await client.PostAsync($"/api/v1/accounts/{_cashAccountId}/bank-transactions/parse-preview", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── POST import ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_ImportsNewRowsAndSkipsDuplicates()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BankTransactions.Add(new BankTransaction
            {
                OrganisationId = _orgId, AccountId = _cashAccountId,
                Date = new DateOnly(2025, 6, 1), Amount = 500.00m, Description = "Existing"
            });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var body = new
        {
            transactions = new object[]
            {
                new { rowIndex = 0, date = "2025-06-02", amount = 100.00m, description = "New row", reference = (string?)null, isDuplicate = false, parseError = (string?)null },
                new { rowIndex = 1, date = "2025-06-01", amount = 500.00m, description = "Existing", reference = (string?)null, isDuplicate = true, parseError = (string?)null }
            }
        };

        var response = await client.PostAsJsonAsync($"/api/v1/accounts/{_cashAccountId}/bank-transactions/import", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("imported").GetInt32());
        Assert.Equal(1, json.GetProperty("duplicates").GetInt32());
        Assert.Equal(0, json.GetProperty("skipped").GetInt32());

        var listResponse = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/bank-transactions");
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("description").GetString() == "New row");
    }

    [Fact]
    public async Task Import_UnknownAccount_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var body = new { transactions = new[] { new { rowIndex = 0, date = "2025-06-02", amount = 100.00m, description = "New row" } } };

        var response = await client.PostAsJsonAsync("/api/v1/accounts/999999/bank-transactions/import", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_CrossTenantAccount_Returns404()
    {
        var (_, _, otherAccountId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var body = new { transactions = new[] { new { rowIndex = 0, date = "2025-06-02", amount = 100.00m, description = "New row" } } };

        var response = await client.PostAsJsonAsync($"/api/v1/accounts/{otherAccountId}/bank-transactions/import", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithoutToken_Returns401()
    {
        var body = new { transactions = new[] { new { rowIndex = 0, date = "2025-06-02", amount = 100.00m, description = "New row" } } };
        var response = await _client.PostAsJsonAsync($"/api/v1/accounts/{_cashAccountId}/bank-transactions/import", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_EmptyTransactions_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/accounts/{_cashAccountId}/bank-transactions/import", new { transactions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── POST suggest-contra ─────────────────────────────────────────────────

    [Fact]
    public async Task SuggestContra_NoHistory_FallsBackToLegalFormDefault()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/accounts/{_cashAccountId}/bank-transactions/suggest-contra",
            new { description = "Some payment", amount = 100m });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_contraAccountId, json.GetProperty("accountId").GetInt32());
    }

    [Fact]
    public async Task SuggestContra_UnknownAccount_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            "/api/v1/accounts/999999/bank-transactions/suggest-contra", new { description = "x", amount = 1m });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SuggestContra_CrossTenantAccount_Returns404()
    {
        var (_, _, otherAccountId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/accounts/{otherAccountId}/bank-transactions/suggest-contra", new { description = "x", amount = 1m });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SuggestContra_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accounts/{_cashAccountId}/bank-transactions/suggest-contra", new { description = "x", amount = 1m });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST set-status ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetStatus_UpdatesStatus()
    {
        int txId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tx = new BankTransaction
            {
                OrganisationId = _orgId, AccountId = _cashAccountId,
                Date = new DateOnly(2025, 6, 1), Amount = 100m, Description = "To be ignored"
            };
            db.BankTransactions.Add(tx);
            await db.SaveChangesAsync();
            txId = tx.Id;
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/bank-transactions/{txId}/set-status", new { status = "Ignored" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ignored", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SetStatus_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/bank-transactions/999999/set-status", new { status = "Ignored" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetStatus_CrossTenant_Returns404()
    {
        var (otherOrgId, _, otherAccountId) = await SeedSecondTenantAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherTx = new BankTransaction
        {
            OrganisationId = otherOrgId, AccountId = otherAccountId,
            Date = new DateOnly(2025, 6, 1), Amount = 100m, Description = "Other tenant tx"
        };
        db.BankTransactions.Add(otherTx);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/bank-transactions/{otherTx.Id}/set-status", new { status = "Ignored" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetStatus_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/bank-transactions/1/set-status", new { status = "Ignored" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── POST match-to-entry ──────────────────────────────────────────────────

    [Fact]
    public async Task MatchToEntry_MatchesTransaction()
    {
        int txId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tx = new BankTransaction
            {
                OrganisationId = _orgId, AccountId = _cashAccountId,
                Date = new DateOnly(2025, 6, 1), Amount = 1000m, Description = "Sale deposit"
            };
            db.BankTransactions.Add(tx);
            await db.SaveChangesAsync();
            txId = tx.Id;
        }

        var client = await AuthenticatedClientAsync();
        var entryId = await CreateAndPostEntryAsync(client, new DateOnly(2025, 6, 1), _cashAccountId, _revenueAccountId, 1000m, "Sale");

        var response = await client.PostAsJsonAsync($"/api/v1/bank-transactions/{txId}/match-to-entry", new { journalEntryId = entryId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Matched", json.GetProperty("status").GetString());
        Assert.Equal(entryId, json.GetProperty("journalEntryId").GetInt32());
    }

    [Fact]
    public async Task MatchToEntry_UnknownTransaction_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var entryId = await CreateAndPostEntryAsync(client, new DateOnly(2025, 6, 1), _cashAccountId, _revenueAccountId, 1000m, "Sale");

        var response = await client.PostAsJsonAsync("/api/v1/bank-transactions/999999/match-to-entry", new { journalEntryId = entryId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MatchToEntry_CrossTenantTransaction_Returns404()
    {
        var (otherOrgId, _, otherAccountId) = await SeedSecondTenantAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherTx = new BankTransaction
        {
            OrganisationId = otherOrgId, AccountId = otherAccountId,
            Date = new DateOnly(2025, 6, 1), Amount = 100m, Description = "Other tenant tx"
        };
        db.BankTransactions.Add(otherTx);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var entryId = await CreateAndPostEntryAsync(client, new DateOnly(2025, 6, 1), _cashAccountId, _revenueAccountId, 1000m, "Sale");

        var response = await client.PostAsJsonAsync($"/api/v1/bank-transactions/{otherTx.Id}/match-to-entry", new { journalEntryId = entryId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MatchToEntry_UnknownJournalEntry_Returns400()
    {
        int txId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tx = new BankTransaction
            {
                OrganisationId = _orgId, AccountId = _cashAccountId,
                Date = new DateOnly(2025, 6, 1), Amount = 1000m, Description = "Sale deposit"
            };
            db.BankTransactions.Add(tx);
            await db.SaveChangesAsync();
            txId = tx.Id;
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/bank-transactions/{txId}/match-to-entry", new { journalEntryId = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MatchToEntry_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/bank-transactions/1/match-to-entry", new { journalEntryId = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
