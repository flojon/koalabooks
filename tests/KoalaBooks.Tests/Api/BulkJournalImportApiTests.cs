using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace KoalaBooks.Tests;

// Covers the BulkJournalImportController endpoint added for issue #122 (Agent H stream,
// 5.H-2): all-or-nothing transactional batch import of journal entries — the first invalid
// entry in the batch rolls back every entry already created in that call, unlike the
// partial-success convention used by SieImportAllResult/ZipImportResult elsewhere (this is
// a direct financial write). Kept in its own file per the program plan's guidance.
public class BulkJournalImportApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;
    private int _cashAccountId;
    private int _revenueAccountId;

    private const string TestEmail = "api-test-bulk-journal-import@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _fiscalYearId, _cashAccountId, _revenueAccountId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int fiscalYearId, int cashAccountId, int revenueAccountId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Bulk Journal Import)", Slug = "api-test-bulk-journal-import", LegalForm = LegalForm.Aktiebolag };
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

        return (org.Id, fy.Id, cash.Id, revenue.Id);
    }

    private async Task<(int orgId, int fiscalYearId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-bulk-journal-import", LegalForm = LegalForm.Aktiebolag };
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

    private object ValidEntry(string date = "2025-06-01", decimal amount = 100m) => new
    {
        date,
        description = "Sale",
        lines = new[]
        {
            new { accountId = _cashAccountId, debitAmount = amount, creditAmount = 0m },
            new { accountId = _revenueAccountId, debitAmount = 0m, creditAmount = amount }
        }
    };

    private object UnbalancedEntry(string date = "2025-06-02") => new
    {
        date,
        description = "Broken",
        lines = new[]
        {
            new { accountId = _cashAccountId, debitAmount = 100m, creditAmount = 0m },
            new { accountId = _revenueAccountId, debitAmount = 0m, creditAmount = 50m }
        }
    };

    // ── POST bulk-import ──────────────────────────────────────────────────────

    [Fact]
    public async Task Import_AllValidEntries_CreatesAllAndReturnsIds()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries/bulk-import",
            new { entries = new[] { ValidEntry("2025-06-01", 100m), ValidEntry("2025-06-02", 200m) } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(2, json.GetProperty("createdEntryIds").GetArrayLength());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.FiscalYearId == _fiscalYearId);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Import_OneInvalidEntry_RollsBackWholeBatch()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries/bulk-import",
            new { entries = new[] { ValidEntry("2025-06-01", 100m), UnbalancedEntry("2025-06-02"), ValidEntry("2025-06-03", 300m) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The failure body must surface which entry broke the batch (index 1, the unbalanced
        // one) — a caller building a bulk-import UI has no other way to point the user at the
        // offending row. Regression test for a bug where this reached the client as a bare
        // ProblemDetails with the index silently discarded.
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(1, json.GetProperty("failedEntryIndex").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("error").GetString()));
        Assert.Equal(0, json.GetProperty("createdEntryIds").GetArrayLength());

        // Neither the valid entry before the bad one, nor the valid entry after it, should
        // have been persisted — the whole batch rolls back on the first failure.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.FiscalYearId == _fiscalYearId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Import_EntryReferencingAccountFromAnotherFiscalYear_Returns400AndPersistsNothing()
    {
        // Regression test for CreateManyAsync's hoisted-query path: the valid account-id set
        // is fetched once for the whole batch, so a wrong FiscalYearId leak in that single
        // query would silently let entries through for every request, not just occasionally.
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Accounts.Add(new Account
            {
                AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset,
                FiscalYearId = otherFiscalYearId, IsActive = true
            });
            await db.SaveChangesAsync();
            var otherAccount = await db.Accounts.IgnoreQueryFilters()
                .SingleAsync(a => a.FiscalYearId == otherFiscalYearId);

            var client = await AuthenticatedClientAsync();
            var response = await client.PostAsJsonAsync(
                $"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries/bulk-import",
                new
                {
                    entries = new[]
                    {
                        new
                        {
                            date = "2025-06-01",
                            description = "Cross-tenant account",
                            lines = new[]
                            {
                                new { accountId = otherAccount.Id, debitAmount = 100m, creditAmount = 0m },
                                new { accountId = _revenueAccountId, debitAmount = 0m, creditAmount = 100m }
                            }
                        }
                    }
                });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, json.GetProperty("failedEntryIndex").GetInt32());
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await verifyDb.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.FiscalYearId == _fiscalYearId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Import_EmptyEntries_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries/bulk-import",
            new { entries = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            "/api/v1/fiscal-years/999999/journal-entries/bulk-import",
            new { entries = new[] { ValidEntry() } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_CrossTenantFiscalYear_Returns404()
    {
        var (_, otherFiscalYearId) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{otherFiscalYearId}/journal-entries/bulk-import",
            new { entries = new[] { ValidEntry() } });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/journal-entries/bulk-import",
            new { entries = new[] { ValidEntry() } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
