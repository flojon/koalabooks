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

// Covers the four remaining SupplierInvoices verbs added for issue #122 (Agent C stream):
// from-entry, post, mark-paid, find-matching-bank-tx. Kept in its own file rather than
// growing ApiTests.cs further (already 1800+ lines) per the program plan's guidance.
public class SupplierInvoicesRemainingVerbsApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;
    private int _cashAccountId;
    private int _expenseAccountId;
    private int _payableAccountId;
    private int _vatAccountId;

    private const string TestEmail = "api-test-si-verbs@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (SI verbs)", Slug = "api-test-si-verbs", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();
        _orgId = org.Id;

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
        _fiscalYearId = fy.Id;

        var cash = new Account { AccountNumber = "1910", Name = "Cash", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true };
        var expense = new Account { AccountNumber = "4000", Name = "Purchases", AccountClass = AccountClass.Expense, FiscalYearId = fy.Id, IsActive = true };
        var payable = new Account { AccountNumber = "2440", Name = "Accounts payable", AccountClass = AccountClass.Liability, FiscalYearId = fy.Id, IsActive = true };
        var vat = new Account { AccountNumber = "2640", Name = "Input VAT", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true };
        db.Accounts.AddRange(cash, expense, payable, vat);
        await db.SaveChangesAsync();

        _cashAccountId = cash.Id;
        _expenseAccountId = expense.Id;
        _payableAccountId = payable.Id;
        _vatAccountId = vat.Id;
    }

    private async Task<(int orgId, int fiscalYearId, int accountId)> SeedSecondTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-si-verbs", LegalForm = LegalForm.Aktiebolag };
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

    private async Task<int> CreateInvoiceAsync(HttpClient client, decimal totalAmount = 1000m, string supplierName = "Acme AB")
    {
        var body = new
        {
            supplierName,
            invoiceDate = "2025-03-01",
            dueDate = "2025-03-31",
            amountExclVat = totalAmount * 0.8m,
            vatAmount = totalAmount * 0.2m,
            totalAmount
        };
        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices", body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetInt32();
    }

    private async Task<int> SeedJournalEntryAsync(int fiscalYearId, int accountId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entry = new JournalEntry
        {
            FiscalYearId = fiscalYearId,
            Date = new DateOnly(2025, 3, 1),
            Description = "Unlinked entry",
            Lines =
            [
                new JournalEntryLine { AccountId = accountId, DebitAmount = 100, CreditAmount = 0 },
                new JournalEntryLine { AccountId = accountId, DebitAmount = 0, CreditAmount = 100 }
            ]
        };
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    // ── from-entry ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FromEntry_ValidEntry_Returns201WithLinkedInvoice()
    {
        var client = await AuthenticatedClientAsync();
        var entryId = await SeedJournalEntryAsync(_fiscalYearId, _expenseAccountId);

        var body = new
        {
            journalEntryId = entryId,
            supplierName = "From Entry Supplier",
            invoiceDate = "2025-03-01",
            dueDate = "2025-03-31",
            amountExclVat = 800m,
            vatAmount = 200m,
            totalAmount = 1000m
        };

        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices/from-entry", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("From Entry Supplier", json.GetProperty("supplierName").GetString());
        Assert.Equal(entryId, json.GetProperty("journalEntryId").GetInt32());
    }

    [Fact]
    public async Task FromEntry_AlreadyLinkedEntry_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var entryId = await SeedJournalEntryAsync(_fiscalYearId, _expenseAccountId);

        var body = new
        {
            journalEntryId = entryId,
            supplierName = "First link",
            invoiceDate = "2025-03-01",
            dueDate = "2025-03-31",
            amountExclVat = 800m,
            vatAmount = 200m,
            totalAmount = 1000m
        };
        var first = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices/from-entry", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices/from-entry", body);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task FromEntry_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var body = new
        {
            journalEntryId = 1,
            supplierName = "Nope",
            invoiceDate = "2025-03-01",
            dueDate = "2025-03-31",
            amountExclVat = 80m,
            vatAmount = 20m,
            totalAmount = 100m
        };
        var response = await client.PostAsJsonAsync("/api/v1/fiscal-years/999999/supplier-invoices/from-entry", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FromEntry_CrossTenantFiscalYear_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();
        var client = await AuthenticatedClientAsync();

        var body = new
        {
            journalEntryId = 1,
            supplierName = "Nope",
            invoiceDate = "2025-03-01",
            dueDate = "2025-03-31",
            amountExclVat = 80m,
            vatAmount = 20m,
            totalAmount = 100m
        };
        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{otherFiscalYearId}/supplier-invoices/from-entry", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FromEntry_WithoutToken_Returns401()
    {
        var body = new
        {
            journalEntryId = 1,
            supplierName = "Nope",
            invoiceDate = "2025-03-01",
            dueDate = "2025-03-31",
            amountExclVat = 80m,
            vatAmount = 20m,
            totalAmount = 100m
        };
        var response = await _client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/supplier-invoices/from-entry", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── post ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_ValidInvoice_Returns200WithJournalEntryLinked()
    {
        var client = await AuthenticatedClientAsync();
        var invoiceId = await CreateInvoiceAsync(client);

        var body = new
        {
            expenseAccountId = _expenseAccountId,
            payableAccountId = _payableAccountId,
            vatAccountId = _vatAccountId
        };
        var response = await client.PostAsJsonAsync($"/api/v1/supplier-invoices/{invoiceId}/post", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("journalEntryId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Post_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var body = new
        {
            expenseAccountId = _expenseAccountId,
            payableAccountId = _payableAccountId,
            vatAccountId = _vatAccountId
        };
        var response = await client.PostAsJsonAsync("/api/v1/supplier-invoices/999999/post", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, otherAccountId) = await SeedSecondTenantAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherInvoice = new SupplierInvoice
        {
            FiscalYearId = otherFiscalYearId,
            SupplierName = "Other tenant supplier",
            InvoiceDate = new DateOnly(2025, 1, 15),
            DueDate = new DateOnly(2025, 2, 15),
            AmountExclVat = 80m,
            VatAmount = 20m,
            TotalAmount = 100m
        };
        db.SupplierInvoices.Add(otherInvoice);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var body = new { expenseAccountId = otherAccountId, payableAccountId = otherAccountId, vatAccountId = (int?)null };
        var response = await client.PostAsJsonAsync($"/api/v1/supplier-invoices/{otherInvoice.Id}/post", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithoutToken_Returns401()
    {
        var body = new { expenseAccountId = 1, payableAccountId = 1, vatAccountId = (int?)null };
        var response = await _client.PostAsJsonAsync("/api/v1/supplier-invoices/1/post", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── mark-paid ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsPaid_ValidInvoice_Returns200AndSetsIsPaid()
    {
        var client = await AuthenticatedClientAsync();
        var invoiceId = await CreateInvoiceAsync(client);

        var body = new
        {
            paidDate = "2025-03-15",
            bankAccountId = _cashAccountId,
            payableAccountId = _payableAccountId
        };
        var response = await client.PostAsJsonAsync($"/api/v1/supplier-invoices/{invoiceId}/mark-paid", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isPaid").GetBoolean());
        Assert.Equal("2025-03-15", json.GetProperty("paidDate").GetString());
    }

    [Fact]
    public async Task MarkAsPaid_WithLinkedBankTransaction_MatchesTransaction()
    {
        var client = await AuthenticatedClientAsync();
        var invoiceId = await CreateInvoiceAsync(client);

        int bankTxId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tx = new BankTransaction
            {
                OrganisationId = _orgId, AccountId = _cashAccountId,
                Date = new DateOnly(2025, 3, 15), Amount = -1000m, Description = "Payment to Acme"
            };
            db.BankTransactions.Add(tx);
            await db.SaveChangesAsync();
            bankTxId = tx.Id;
        }

        var body = new
        {
            paidDate = "2025-03-15",
            bankAccountId = _cashAccountId,
            payableAccountId = _payableAccountId,
            linkBankTransactionId = bankTxId
        };
        var response = await client.PostAsJsonAsync($"/api/v1/supplier-invoices/{invoiceId}/mark-paid", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await verifyDb.BankTransactions.IgnoreQueryFilters().FirstAsync(b => b.Id == bankTxId);
        Assert.Equal(BankTransactionStatus.Matched, reloaded.Status);
        Assert.NotNull(reloaded.JournalEntryId);
    }

    [Fact]
    public async Task MarkAsPaid_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var body = new { paidDate = "2025-03-15", bankAccountId = _cashAccountId, payableAccountId = _payableAccountId };
        var response = await client.PostAsJsonAsync("/api/v1/supplier-invoices/999999/mark-paid", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MarkAsPaid_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, otherAccountId) = await SeedSecondTenantAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherInvoice = new SupplierInvoice
        {
            FiscalYearId = otherFiscalYearId,
            SupplierName = "Other tenant supplier",
            InvoiceDate = new DateOnly(2025, 1, 15),
            DueDate = new DateOnly(2025, 2, 15),
            AmountExclVat = 80m,
            VatAmount = 20m,
            TotalAmount = 100m
        };
        db.SupplierInvoices.Add(otherInvoice);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var body = new { paidDate = "2025-01-20", bankAccountId = otherAccountId, payableAccountId = otherAccountId };
        var response = await client.PostAsJsonAsync($"/api/v1/supplier-invoices/{otherInvoice.Id}/mark-paid", body);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MarkAsPaid_WithoutToken_Returns401()
    {
        var body = new { paidDate = "2025-03-15", bankAccountId = 1, payableAccountId = 1 };
        var response = await _client.PostAsJsonAsync("/api/v1/supplier-invoices/1/mark-paid", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── find-matching-bank-tx ──────────────────────────────────────────────────

    [Fact]
    public async Task FindMatchingBankTx_ReturnsMatchingTransaction()
    {
        var client = await AuthenticatedClientAsync();
        var invoiceId = await CreateInvoiceAsync(client, totalAmount: 1000m);

        int bankTxId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tx = new BankTransaction
            {
                OrganisationId = _orgId, AccountId = _cashAccountId,
                Date = new DateOnly(2025, 3, 10), Amount = -1000m, Description = "Payment to Acme"
            };
            db.BankTransactions.Add(tx);
            await db.SaveChangesAsync();
            bankTxId = tx.Id;
        }

        var response = await client.GetAsync($"/api/v1/supplier-invoices/{invoiceId}/find-matching-bank-tx");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Contains(items, i => i.GetProperty("id").GetInt32() == bankTxId);
    }

    [Fact]
    public async Task FindMatchingBankTx_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/supplier-invoices/999999/find-matching-bank-tx");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FindMatchingBankTx_CrossTenant_Returns404()
    {
        var (_, otherFiscalYearId, _) = await SeedSecondTenantAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherInvoice = new SupplierInvoice
        {
            FiscalYearId = otherFiscalYearId,
            SupplierName = "Other tenant supplier",
            InvoiceDate = new DateOnly(2025, 1, 15),
            DueDate = new DateOnly(2025, 2, 15),
            AmountExclVat = 80m,
            VatAmount = 20m,
            TotalAmount = 100m
        };
        db.SupplierInvoices.Add(otherInvoice);
        await db.SaveChangesAsync();

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/supplier-invoices/{otherInvoice.Id}/find-matching-bank-tx");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FindMatchingBankTx_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/supplier-invoices/1/find-matching-bank-tx");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
