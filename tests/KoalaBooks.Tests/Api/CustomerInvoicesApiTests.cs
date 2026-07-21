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

// Covers CustomerInvoicesController, added for issue #122's "Agent E" stream (folded into #290).
public class CustomerInvoicesApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _fiscalYearId;
    private int _invoiceId;
    private int _receivableAccountId;
    private int _revenueAccountId;
    private int _vatAccountId;
    private int _bankAccountId;

    private const string TestEmail = "api-test-customer-invoices@koalabooks.test";
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

        var org = new Organisation { Name = "API Test Org (CustomerInvoices)", Slug = "api-test-customer-invoices", LegalForm = LegalForm.Aktiebolag };
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
            OrganisationId = org.Id, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31)
        };
        db.FiscalYears.Add(fy);
        await db.SaveChangesAsync();
        _fiscalYearId = fy.Id;

        var receivable = new Account { AccountNumber = "1510", Name = "Kundfordringar", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true };
        var revenue = new Account { AccountNumber = "3001", Name = "Försäljning", AccountClass = AccountClass.Revenue, FiscalYearId = fy.Id, IsActive = true };
        var vat = new Account { AccountNumber = "2610", Name = "Utgående moms", AccountClass = AccountClass.Liability, FiscalYearId = fy.Id, IsActive = true };
        var bank = new Account { AccountNumber = "1930", Name = "Bank", AccountClass = AccountClass.Asset, FiscalYearId = fy.Id, IsActive = true };
        db.Accounts.AddRange(receivable, revenue, vat, bank);
        await db.SaveChangesAsync();
        _receivableAccountId = receivable.Id;
        _revenueAccountId = revenue.Id;
        _vatAccountId = vat.Id;
        _bankAccountId = bank.Id;

        var invoice = new CustomerInvoice
        {
            FiscalYearId = fy.Id,
            CustomerName = "Acme AB",
            InvoiceNumber = 1,
            InvoiceDate = new DateOnly(2026, 7, 1),
            DueDate = new DateOnly(2026, 7, 31),
            AmountExclVat = 1000, VatAmount = 250, TotalAmount = 1250,
            Lines = [new CustomerInvoiceLine { Description = "Konsulttjänst", Quantity = 1, UnitPrice = 1000, VatRate = 25, AmountExclVat = 1000, VatAmount = 250, TotalAmount = 1250 }]
        };
        db.CustomerInvoices.Add(invoice);
        await db.SaveChangesAsync();
        _invoiceId = invoice.Id;
    }

    private async Task<int> SeedOtherTenantFiscalYearAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-customer-invoices", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var fy2 = new FiscalYear
        {
            OrganisationId = org2.Id, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31)
        };
        db.FiscalYears.Add(fy2);
        await db.SaveChangesAsync();
        return fy2.Id;
    }

    private async Task<int> SeedOtherTenantCustomerInvoiceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-customer-invoices-inv", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var fy2 = new FiscalYear
        {
            OrganisationId = org2.Id, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31)
        };
        db.FiscalYears.Add(fy2);
        await db.SaveChangesAsync();

        var invoice2 = new CustomerInvoice
        {
            FiscalYearId = fy2.Id,
            CustomerName = "Other Customer",
            InvoiceNumber = 1,
            InvoiceDate = new DateOnly(2026, 7, 1),
            DueDate = new DateOnly(2026, 7, 31),
            AmountExclVat = 1000, VatAmount = 250, TotalAmount = 1250,
            Lines = [new CustomerInvoiceLine { Description = "Other line", Quantity = 1, UnitPrice = 1000, VatRate = 25, AmountExclVat = 1000, VatAmount = 250, TotalAmount = 1250 }]
        };
        db.CustomerInvoices.Add(invoice2);
        await db.SaveChangesAsync();
        return invoice2.Id;
    }

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

    [Fact]
    public async Task GetByFiscalYear_ReturnsPagedInvoices()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/customer-invoices");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("totalCount").GetInt32());
        Assert.Equal("Acme AB", json.GetProperty("items")[0].GetProperty("customerName").GetString());
    }

    [Fact]
    public async Task GetByFiscalYear_CrossTenant_Returns404()
    {
        var otherFyId = await SeedOtherTenantFiscalYearAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFyId}/customer-invoices");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetByFiscalYear_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/customer-invoices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_ReturnsInvoiceWithLines()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customer-invoices/{_invoiceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme AB", json.GetProperty("customerName").GetString());
        Assert.Single(json.GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/customer-invoices/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_CrossTenant_Returns404()
    {
        var otherInvoiceId = await SeedOtherTenantCustomerInvoiceAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customer-invoices/{otherInvoiceId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsCreatedInvoice()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/customer-invoices", new
        {
            customerName = "New Customer AB",
            invoiceDate = "2026-07-10",
            dueDate = "2026-08-09",
            lines = new[] { new { description = "Widget", quantity = 2, unitPrice = 500, vatRate = 25 } }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New Customer AB", json.GetProperty("customerName").GetString());
        Assert.Equal(2, json.GetProperty("invoiceNumber").GetInt32()); // 1 already seeded
        Assert.Equal(1250m, json.GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task Create_NoLines_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/customer-invoices", new
        {
            customerName = "New Customer AB",
            invoiceDate = "2026-07-10",
            dueDate = "2026-08-09",
            lines = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/fiscal-years/999999/customer-invoices", new
        {
            customerName = "New Customer AB",
            invoiceDate = "2026-07-10",
            dueDate = "2026-08-09",
            lines = new[] { new { description = "Widget", quantity = 1, unitPrice = 100, vatRate = 25 } }
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/fiscal-years/{_fiscalYearId}/customer-invoices", new
        {
            customerName = "New Customer AB",
            invoiceDate = "2026-07-10",
            dueDate = "2026-08-09",
            lines = new[] { new { description = "Widget", quantity = 1, unitPrice = 100, vatRate = 25 } }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_PostsInvoiceToLedger()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/customer-invoices/{_invoiceId}/post", new
        {
            receivableAccountId = _receivableAccountId,
            revenueAccountId = _revenueAccountId,
            vatRateAccountIds = new Dictionary<int, int> { [25] = _vatAccountId }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isPosted").GetBoolean());
    }

    [Fact]
    public async Task Post_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/customer-invoices/999999/post", new
        {
            receivableAccountId = _receivableAccountId,
            revenueAccountId = _revenueAccountId,
            vatRateAccountIds = new Dictionary<int, int>()
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_CrossTenant_Returns404()
    {
        var otherInvoiceId = await SeedOtherTenantCustomerInvoiceAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/customer-invoices/{otherInvoiceId}/post", new
        {
            receivableAccountId = _receivableAccountId,
            revenueAccountId = _revenueAccountId,
            vatRateAccountIds = new Dictionary<int, int> { [25] = _vatAccountId }
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/customer-invoices/{_invoiceId}/post", new
        {
            receivableAccountId = _receivableAccountId,
            revenueAccountId = _revenueAccountId,
            vatRateAccountIds = new Dictionary<int, int>()
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MarkPaid_MarksInvoicePaid()
    {
        var client = await AuthenticatedClientAsync();
        await client.PostAsJsonAsync($"/api/v1/customer-invoices/{_invoiceId}/post", new
        {
            receivableAccountId = _receivableAccountId,
            revenueAccountId = _revenueAccountId,
            vatRateAccountIds = new Dictionary<int, int> { [25] = _vatAccountId }
        });

        var response = await client.PostAsJsonAsync($"/api/v1/customer-invoices/{_invoiceId}/mark-paid", new
        {
            paidDate = "2026-07-15",
            bankAccountId = _bankAccountId,
            receivableAccountId = _receivableAccountId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("isPaid").GetBoolean());
    }

    [Fact]
    public async Task MarkPaid_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/customer-invoices/999999/mark-paid", new
        {
            paidDate = "2026-07-15",
            bankAccountId = _bankAccountId,
            receivableAccountId = _receivableAccountId
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MarkPaid_CrossTenant_Returns404()
    {
        var otherInvoiceId = await SeedOtherTenantCustomerInvoiceAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"/api/v1/customer-invoices/{otherInvoiceId}/mark-paid", new
        {
            paidDate = "2026-07-15",
            bankAccountId = _bankAccountId,
            receivableAccountId = _receivableAccountId
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MarkPaid_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/customer-invoices/{_invoiceId}/mark-paid", new
        {
            paidDate = "2026-07-15",
            bankAccountId = _bankAccountId,
            receivableAccountId = _receivableAccountId
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FindMatchingBankTx_ReturnsMatches()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BankTransactions.Add(new BankTransaction
            {
                OrganisationId = _orgId,
                AccountId = _bankAccountId, Date = new DateOnly(2026, 7, 5),
                Amount = 1250m, Description = "Inbetalning Acme", Status = BankTransactionStatus.Unmatched
            });
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/customer-invoices/find-matching-bank-tx?invoiceTotal=1250&invoiceDate=2026-07-01&dueDate=2026-07-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Inbetalning Acme", items[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task FindMatchingBankTx_CrossTenant_Returns404()
    {
        var otherFyId = await SeedOtherTenantFiscalYearAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync(
            $"/api/v1/fiscal-years/{otherFyId}/customer-invoices/find-matching-bank-tx?invoiceTotal=1250&invoiceDate=2026-07-01&dueDate=2026-07-31");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FindMatchingBankTx_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync(
            $"/api/v1/fiscal-years/{_fiscalYearId}/customer-invoices/find-matching-bank-tx?invoiceTotal=1250&invoiceDate=2026-07-01&dueDate=2026-07-31");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesUnpostedInvoice()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.DeleteAsync($"/api/v1/customer-invoices/{_invoiceId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/v1/customer-invoices/{_invoiceId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.DeleteAsync("/api/v1/customer-invoices/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_CrossTenant_Returns404()
    {
        var otherInvoiceId = await SeedOtherTenantCustomerInvoiceAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.DeleteAsync($"/api/v1/customer-invoices/{otherInvoiceId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_PostedInvoice_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        await client.PostAsJsonAsync($"/api/v1/customer-invoices/{_invoiceId}/post", new
        {
            receivableAccountId = _receivableAccountId,
            revenueAccountId = _revenueAccountId,
            vatRateAccountIds = new Dictionary<int, int> { [25] = _vatAccountId }
        });

        var response = await client.DeleteAsync($"/api/v1/customer-invoices/{_invoiceId}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutToken_Returns401()
    {
        var response = await _client.DeleteAsync($"/api/v1/customer-invoices/{_invoiceId}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPdf_ReturnsPdfBytes()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customer-invoices/{_invoiceId}/pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF"u8.ToArray(), bytes.Take(4).ToArray());
    }

    [Fact]
    public async Task GetPdf_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/customer-invoices/999999/pdf");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPdf_CrossTenant_Returns404()
    {
        var otherInvoiceId = await SeedOtherTenantCustomerInvoiceAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customer-invoices/{otherInvoiceId}/pdf");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPdf_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/customer-invoices/{_invoiceId}/pdf");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
