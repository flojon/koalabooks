# Issue #290 Download Plumbing (+ #122 Agent E) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build bearer-authed REST endpoints for SIE export and customer-invoice PDF (closing #290's WASM download-plumbing gap), the `CustomersController`/`CustomerInvoicesController` those endpoints depend on (closing #122's "Agent E" stream), and unify file-download delivery onto a stream-based JS interop helper that works identically under Blazor Server and future WASM rendering.

**Architecture:** New `api/v1` MVC controllers under `KoalaBooks.Web/Controllers/Api/`, calling existing (or minimally extended) Application-layer services — no business logic in controllers. `CustomerInvoicePdfGenerator` moves from `KoalaBooks.Web` to `KoalaBooks.Application` (pure function, wrong layer today) so `CustomerInvoiceService` can call it directly. New WASM-side `*ApiService` implementations in `KoalaBooks.Client/Services` follow the existing DI-swap pattern (same interface, HTTP-backed instead of EF-backed). `download.js` gains a `DotNetStreamReference`-based helper that replaces the base64 approach for all three Razor pages, without changing their render mode.

**Tech Stack:** ASP.NET Core MVC controllers, OpenIddict bearer auth, EF Core (Npgsql), Blazor Server (`IJSRuntime`/`DotNetStreamReference`), xUnit + Testcontainers Postgres (`WebApiFactory`, `PostgresContainerFixture`).

## Global Constraints

(Copied from the spec and the #122 program plan — apply to every task below.)

- All routes under `/api/v1/` — no version bump.
- Controllers must not contain business logic — call an existing Application service.
- Every controller: `[ApiController]`, `[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]`, `[Route("api/v1...")]`, constructor-DI of interfaces only.
- `[ProducesResponseType]` (or `[ProducesResponseType<T>]`) on every action, including 401/404/400 as applicable.
- Integration test via `WebApiFactory` + Testcontainers Postgres for every new endpoint — happy path, 401 without token, cross-tenant 404 where the resource is tenant-scoped.
- No render-mode flip: `SieExport.razor`, `VatReport.razor`, `CustomerInvoices.razor` stay Server-rendered (`downloadFileFromBase64` → REST calls are for the WASM-side `*ApiService` implementations only; the Server-rendered pages keep calling the same interfaces in-process, just with new methods/JS helper).
- Customer invoice PDF keeps its "open in a new tab" UX (Blob URL + `window.open`), not a forced download.
- `CreateFromEntryAsync` for customer invoices stays out of scope (deferred, per #122 plan 5.E).
- SIE *import* stays out of scope (Agent H's separate Hangfire-backed stream).

---

## Task 1: Move `CustomerInvoicePdfGenerator` to the Application layer

**Files:**
- Create: `src/KoalaBooks.Application/Services/CustomerInvoicePdfGenerator.cs`
- Delete: `src/KoalaBooks.Web/Services/CustomerInvoicePdfGenerator.cs`
- Modify: `src/KoalaBooks.Application/KoalaBooks.Application.csproj`
- Modify: `src/KoalaBooks.Web/Program.cs:252-259` (namespace reference only, route unchanged for now — Task 5 deletes the route)
- Test: `tests/KoalaBooks.Tests/Application/CustomerInvoicePdfGeneratorTests.cs`

**Interfaces:**
- Produces: `KoalaBooks.Application.Services.CustomerInvoicePdfGenerator.Generate(CustomerInvoice invoice) : byte[]` — same signature as today, new namespace. Task 3 calls this from `CustomerInvoiceService`.

- [ ] **Step 1: Write the failing test for the generator in its new location**

Create `tests/KoalaBooks.Tests/Application/CustomerInvoicePdfGeneratorTests.cs`:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests.Application;

public class CustomerInvoicePdfGeneratorTests
{
    [Fact]
    public void Generate_ProducesNonEmptyPdfBytes()
    {
        var invoice = new CustomerInvoice
        {
            InvoiceNumber = 42,
            CustomerName = "Acme AB",
            InvoiceDate = new DateOnly(2026, 7, 1),
            DueDate = new DateOnly(2026, 7, 31),
            Lines =
            [
                new CustomerInvoiceLine { Description = "Konsulttjänst", Quantity = 1, UnitPrice = 1000, VatRate = 25, AmountExclVat = 1000, VatAmount = 250, TotalAmount = 1250 }
            ],
            AmountExclVat = 1000,
            VatAmount = 250,
            TotalAmount = 1250
        };

        var bytes = CustomerInvoicePdfGenerator.Generate(invoice);

        Assert.NotEmpty(bytes);
        // PDF file magic number.
        Assert.Equal("%PDF"u8.ToArray(), bytes.Take(4).ToArray());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerInvoicePdfGeneratorTests`
Expected: FAIL — compile error, `KoalaBooks.Application.Services.CustomerInvoicePdfGenerator` doesn't exist yet.

- [ ] **Step 3: Move the file and update its namespace**

`git mv src/KoalaBooks.Web/Services/CustomerInvoicePdfGenerator.cs src/KoalaBooks.Application/Services/CustomerInvoicePdfGenerator.cs`

Then edit the moved file's namespace line (only change — everything else in the file stays exactly as-is, it already only depends on `KoalaBooks.Domain.Entities` and QuestPDF):

```csharp
namespace KoalaBooks.Application.Services;
```

- [ ] **Step 4: Add the QuestPDF package reference to Application**

Edit `src/KoalaBooks.Application/KoalaBooks.Application.csproj`, inside the existing `<ItemGroup>` that has `PackageReference` entries:

```xml
  <ItemGroup>
    <PackageReference Include="Hangfire.Core" Version="1.8.24" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
    <PackageReference Include="QuestPDF" Version="2026.7.1" />
  </ItemGroup>
```

- [ ] **Step 5: Update the one remaining reference in Program.cs**

In `src/KoalaBooks.Web/Program.cs`, the existing route (unchanged in this task, deleted in Task 5) currently reads:

```csharp
    var bytes = KoalaBooks.Web.Services.CustomerInvoicePdfGenerator.Generate(invoice);
```

Change to:

```csharp
    var bytes = KoalaBooks.Application.Services.CustomerInvoicePdfGenerator.Generate(invoice);
```

- [ ] **Step 6: Run the new test to verify it passes**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerInvoicePdfGeneratorTests`
Expected: PASS

- [ ] **Step 7: Run the full test suite to confirm nothing else broke**

Run: `dotnet build && dotnet test tests/KoalaBooks.Tests`
Expected: build succeeds, all tests pass (this confirms the old route in Program.cs still compiles and works).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
Move CustomerInvoicePdfGenerator to the Application layer

It only depends on Domain.Entities and QuestPDF — no Web-specific
dependency — so it belongs alongside VatReportCsvExporter, letting
CustomerInvoiceService call it directly without an inverted Web
dependency (Domain <- Infrastructure <- Application <- Web).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `ICustomerService.GetByIdAsync`

**Files:**
- Modify: `src/KoalaBooks.Domain/Interfaces/ICustomerService.cs`
- Modify: `src/KoalaBooks.Application/Services/CustomerService.cs`
- Test: `tests/KoalaBooks.Tests/Services/CustomerServiceTests.cs`

**Interfaces:**
- Produces: `ICustomerService.GetByIdAsync(int id) : Task<Customer?>` — org-scoped via `AppDbContext`'s global query filter (`Customer.OrganisationId == _currentUser.OrganisationId`), same tenant-safety mechanism as every other tenant-scoped entity. Task 4's `CustomersController` consumes this.

- [ ] **Step 1: Write the failing test**

Create `tests/KoalaBooks.Tests/Services/CustomerServiceTests.cs`:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests.Services;

public class CustomerServiceTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly LocalCurrentUser _currentUser;
    private readonly int _organisationId;

    public CustomerServiceTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org-customer-svc" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _organisationId = org.Id;
        _currentUser.OrganisationId = _organisationId;
    }

    public void Dispose()
    {
        _db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsCustomer_WhenBelongsToCurrentTenant()
    {
        var customer = new Customer { OrganisationId = _organisationId, Name = "Acme AB" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var service = new CustomerService(_db);
        var found = await service.GetByIdAsync(customer.Id);

        Assert.NotNull(found);
        Assert.Equal("Acme AB", found!.Name);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenUnknownId()
    {
        var service = new CustomerService(_db);
        var found = await service.GetByIdAsync(999999);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenBelongsToDifferentTenant()
    {
        var otherOrg = new Organisation { Name = "Other Org", Slug = "other-org-customer-svc" };
        _db.Organisations.Add(otherOrg);
        await _db.SaveChangesAsync();

        var otherCustomer = new Customer { OrganisationId = otherOrg.Id, Name = "Other Customer" };
        _db.Customers.Add(otherCustomer);
        await _db.SaveChangesAsync();

        var service = new CustomerService(_db);
        var found = await service.GetByIdAsync(otherCustomer.Id);

        Assert.Null(found);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerServiceTests`
Expected: FAIL — compile error, `ICustomerService`/`CustomerService` have no `GetByIdAsync`.

- [ ] **Step 3: Add the interface member**

In `src/KoalaBooks.Domain/Interfaces/ICustomerService.cs`, add alongside `GetAllAsync`:

```csharp
public interface ICustomerService
{
    Task<List<Customer>> GetAllAsync(int organisationId);
    Task<Customer?> GetByIdAsync(int id);
    Task<(Customer? Customer, string? Error)> CreateAsync(Customer customer);
    Task<(Customer? Customer, string? Error)> UpdateAsync(Customer customer);
    Task<string?> DeactivateAsync(int customerId);
}
```

- [ ] **Step 4: Implement it**

In `src/KoalaBooks.Application/Services/CustomerService.cs`, add alongside `GetAllAsync`:

```csharp
    public async Task<Customer?> GetByIdAsync(int id)
    {
        return await _db.Customers.FirstOrDefaultAsync(c => c.Id == id).ConfigureAwait(false);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerServiceTests`
Expected: PASS (3 tests)

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/ICustomerService.cs src/KoalaBooks.Application/Services/CustomerService.cs tests/KoalaBooks.Tests/Services/CustomerServiceTests.cs
git commit -m "$(cat <<'EOF'
Add ICustomerService.GetByIdAsync

Needed by the new CustomersController (#122 Agent E); org-scoping
comes from AppDbContext's existing global query filter, same as
every other tenant-scoped GetByIdAsync in the codebase.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `ICustomerInvoiceService.GetPdfAsync`

**Files:**
- Modify: `src/KoalaBooks.Domain/Interfaces/ICustomerInvoiceService.cs`
- Modify: `src/KoalaBooks.Application/Services/CustomerInvoiceService.cs`
- Test: `tests/KoalaBooks.Tests/Services/CustomerInvoiceServiceGetPdfTests.cs`

**Interfaces:**
- Consumes: `CustomerInvoicePdfGenerator.Generate(CustomerInvoice) : byte[]` (Task 1).
- Produces: `ICustomerInvoiceService.GetPdfAsync(int id) : Task<byte[]?>` — `null` if the invoice doesn't exist (or belongs to another tenant, via the existing `GetByIdAsync`'s query). Task 5's `CustomerInvoicesController` and Task 11's `CustomerInvoices.razor` consume this.

- [ ] **Step 1: Write the failing test**

Create `tests/KoalaBooks.Tests/Services/CustomerInvoiceServiceGetPdfTests.cs`:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests.Services;

public class CustomerInvoiceServiceGetPdfTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly LocalCurrentUser _currentUser;
    private readonly int _fiscalYearId;

    public CustomerInvoiceServiceGetPdfTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org-inv-pdf" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _currentUser.OrganisationId = org.Id;

        var fiscalYear = new FiscalYear
        {
            OrganisationId = org.Id, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31)
        };
        _db.FiscalYears.Add(fiscalYear);
        _db.SaveChanges();
        _fiscalYearId = fiscalYear.Id;
    }

    public void Dispose()
    {
        _db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    [Fact]
    public async Task GetPdfAsync_ReturnsNonEmptyPdfBytes_ForExistingInvoice()
    {
        var service = new CustomerInvoiceService(_db);
        var (invoice, error) = await service.CreateAsync(
            new CustomerInvoice
            {
                FiscalYearId = _fiscalYearId,
                CustomerName = "Acme AB",
                InvoiceDate = new DateOnly(2026, 7, 1),
                DueDate = new DateOnly(2026, 7, 31),
            },
            [new CustomerInvoiceLine { Description = "Konsulttjänst", Quantity = 1, UnitPrice = 1000, VatRate = 25 }]);
        Assert.Null(error);

        var bytes = await service.GetPdfAsync(invoice!.Id);

        Assert.NotNull(bytes);
        Assert.Equal("%PDF"u8.ToArray(), bytes!.Take(4).ToArray());
    }

    [Fact]
    public async Task GetPdfAsync_ReturnsNull_ForUnknownId()
    {
        var service = new CustomerInvoiceService(_db);
        var bytes = await service.GetPdfAsync(999999);

        Assert.Null(bytes);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerInvoiceServiceGetPdfTests`
Expected: FAIL — compile error, `ICustomerInvoiceService`/`CustomerInvoiceService` have no `GetPdfAsync`.

- [ ] **Step 3: Add the interface member**

In `src/KoalaBooks.Domain/Interfaces/ICustomerInvoiceService.cs`, add alongside `GetByIdAsync`:

```csharp
    Task<CustomerInvoice?> GetByIdAsync(int id);
    Task<byte[]?> GetPdfAsync(int id);
```

- [ ] **Step 4: Implement it**

In `src/KoalaBooks.Application/Services/CustomerInvoiceService.cs`, add alongside `GetByIdAsync`:

```csharp
    public async Task<byte[]?> GetPdfAsync(int id)
    {
        var invoice = await GetByIdAsync(id).ConfigureAwait(false);
        return invoice is null ? null : CustomerInvoicePdfGenerator.Generate(invoice);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerInvoiceServiceGetPdfTests`
Expected: PASS (2 tests)

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Domain/Interfaces/ICustomerInvoiceService.cs src/KoalaBooks.Application/Services/CustomerInvoiceService.cs tests/KoalaBooks.Tests/Services/CustomerInvoiceServiceGetPdfTests.cs
git commit -m "$(cat <<'EOF'
Add ICustomerInvoiceService.GetPdfAsync

Wraps GetByIdAsync + CustomerInvoicePdfGenerator so PDF byte
generation is reachable from a REST controller (#290) without
duplicating the invoice lookup, and stays in-process for Server
rendering exactly like today.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `CustomersController`

**Files:**
- Create: `src/KoalaBooks.Web/Models/Api/CustomerResponse.cs`
- Create: `src/KoalaBooks.Web/Models/Api/CreateCustomerRequest.cs`
- Create: `src/KoalaBooks.Web/Models/Api/UpdateCustomerRequest.cs`
- Create: `src/KoalaBooks.Web/Controllers/Api/CustomersController.cs`
- Test: `tests/KoalaBooks.Tests/Api/CustomersApiTests.cs`

**Interfaces:**
- Consumes: `ICustomerService` (`GetAllAsync(int organisationId)`, `GetByIdAsync(int id)` from Task 2, `CreateAsync(Customer)`, `UpdateAsync(Customer)`, `DeactivateAsync(int)`), `ICurrentUser.OrganisationId`.
- Produces: routes `GET/POST api/v1/customers`, `GET/PUT api/v1/customers/{id}`, `POST api/v1/customers/{id}/deactivate`.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/KoalaBooks.Tests/Api/CustomersApiTests.cs`:

```csharp
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

// Covers CustomersController, added for issue #122's "Agent E" stream (folded into #290).
public class CustomersApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _orgId;
    private int _customerId;

    private const string TestEmail = "api-test-customers@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        (_orgId, _customerId) = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<(int orgId, int customerId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (Customers)", Slug = "api-test-customers", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        var user = new ApplicationUser
        {
            UserName = TestEmail, Email = TestEmail,
            EmailConfirmed = true, OrganisationId = org.Id, DisplayName = "API Tester"
        };
        var result = await userManager.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

        var customer = new Customer { OrganisationId = org.Id, Name = "Acme AB" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return (org.Id, customer.Id);
    }

    private async Task<int> SeedOtherTenantCustomerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-customers", LegalForm = LegalForm.Aktiebolag };
        db.Organisations.Add(org2);
        await db.SaveChangesAsync();

        var customer = new Customer { OrganisationId = org2.Id, Name = "Other Customer" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return customer.Id;
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
    public async Task GetAll_ReturnsCustomersForCurrentOrg()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/customers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Acme AB", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetAll_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/v1/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_ReturnsCustomer()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customers/{_customerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme AB", json.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/customers/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_CrossTenant_Returns404()
    {
        var otherCustomerId = await SeedOtherTenantCustomerAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/customers/{otherCustomerId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsCreatedCustomer()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { name = "New Customer AB", country = "SE" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New Customer AB", json.GetProperty("name").GetString());
        Assert.True(json.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Create_MissingName_Returns400()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutToken_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/customers", new { name = "New Customer AB" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Update_UpdatesFields()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync($"/api/v1/customers/{_customerId}", new { name = "Acme Renamed AB", email = "info@acme.example" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Acme Renamed AB", json.GetProperty("name").GetString());
        Assert.Equal("info@acme.example", json.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync("/api/v1/customers/999999", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_CrossTenant_Returns404()
    {
        var otherCustomerId = await SeedOtherTenantCustomerAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync($"/api/v1/customers/{otherCustomerId}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithoutToken_Returns401()
    {
        var response = await _client.PutAsJsonAsync($"/api/v1/customers/{_customerId}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_SetsIsActiveFalse()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/customers/{_customerId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var listResponse = await client.GetAsync("/api/v1/customers");
        var json = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.EnumerateArray()); // GetAllAsync only returns active customers
    }

    [Fact]
    public async Task Deactivate_UnknownId_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/customers/999999/deactivate", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_CrossTenant_Returns404()
    {
        var otherCustomerId = await SeedOtherTenantCustomerAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/customers/{otherCustomerId}/deactivate", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_WithoutToken_Returns401()
    {
        var response = await _client.PostAsync($"/api/v1/customers/{_customerId}/deactivate", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomersApiTests`
Expected: FAIL — compile error, no `CustomersController`/DTOs yet.

- [ ] **Step 3: Create the response DTO**

Create `src/KoalaBooks.Web/Models/Api/CustomerResponse.cs`:

```csharp
namespace KoalaBooks.Web.Models.Api;

public record CustomerResponse(
    int Id,
    int OrganisationId,
    string Name,
    string? OrgNumber,
    string? Email,
    string? Phone,
    string? Address,
    string? PostalCode,
    string? City,
    string Country,
    bool IsActive,
    DateTime CreatedAt);
```

- [ ] **Step 4: Create the request DTOs**

Create `src/KoalaBooks.Web/Models/Api/CreateCustomerRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class CreateCustomerRequest
{
    [Required]
    public string Name { get; init; } = "";

    public string? OrgNumber { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public string Country { get; init; } = "SE";
}
```

Create `src/KoalaBooks.Web/Models/Api/UpdateCustomerRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class UpdateCustomerRequest
{
    [Required]
    public string Name { get; init; } = "";

    public string? OrgNumber { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? PostalCode { get; init; }
    public string? City { get; init; }
    public string Country { get; init; } = "SE";
}
```

- [ ] **Step 5: Create the controller**

Create `src/KoalaBooks.Web/Controllers/Api/CustomersController.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customerService;
    private readonly ICurrentUser _currentUser;

    public CustomersController(ICustomerService customerService, ICurrentUser currentUser)
    {
        _customerService = customerService;
        _currentUser = currentUser;
    }

    [HttpGet("customers")]
    [ProducesResponseType<List<CustomerResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAll()
    {
        var customers = await _customerService.GetAllAsync(_currentUser.OrganisationId ?? 0);
        return Ok(customers.Select(MapCustomer).ToList());
    }

    [HttpGet("customers/{id:int}")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var customer = await _customerService.GetByIdAsync(id);
        if (customer is null) return NotFound();
        return Ok(MapCustomer(customer));
    }

    [HttpPost("customers")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateCustomerRequest request)
    {
        var customer = new Customer
        {
            OrganisationId = _currentUser.OrganisationId ?? 0,
            Name = request.Name,
            OrgNumber = request.OrgNumber,
            Email = request.Email,
            Phone = request.Phone,
            Address = request.Address,
            PostalCode = request.PostalCode,
            City = request.City,
            Country = request.Country
        };

        var (created, error) = await _customerService.CreateAsync(customer);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapCustomer(created));
    }

    [HttpPut("customers/{id:int}")]
    [ProducesResponseType<CustomerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCustomerRequest request)
    {
        var existing = await _customerService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var customer = new Customer
        {
            Id = id,
            Name = request.Name,
            OrgNumber = request.OrgNumber,
            Email = request.Email,
            Phone = request.Phone,
            Address = request.Address,
            PostalCode = request.PostalCode,
            City = request.City,
            Country = request.Country
        };

        var (updated, error) = await _customerService.UpdateAsync(customer);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapCustomer(updated!));
    }

    [HttpPost("customers/{id:int}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(int id)
    {
        var existing = await _customerService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var error = await _customerService.DeactivateAsync(id);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    private static CustomerResponse MapCustomer(Customer c) =>
        new(c.Id, c.OrganisationId, c.Name, c.OrgNumber, c.Email, c.Phone,
            c.Address, c.PostalCode, c.City, c.Country, c.IsActive, c.CreatedAt);
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomersApiTests`
Expected: PASS (all tests)

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Web/Models/Api/CustomerResponse.cs src/KoalaBooks.Web/Models/Api/CreateCustomerRequest.cs src/KoalaBooks.Web/Models/Api/UpdateCustomerRequest.cs src/KoalaBooks.Web/Controllers/Api/CustomersController.cs tests/KoalaBooks.Tests/Api/CustomersApiTests.cs
git commit -m "$(cat <<'EOF'
Add CustomersController REST endpoints (#122 Agent E)

list/by-id/create/update/deactivate, bearer-authed, mirroring the
existing AccountsController/SupplierInvoicesController shape. First
controller to inject ICurrentUser directly, since Customer is
org-scoped rather than fiscal-year-scoped.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `CustomerInvoicesController`

**Files:**
- Create: `src/KoalaBooks.Web/Models/Api/CustomerInvoiceResponse.cs` (includes `CustomerInvoiceLineResponse`)
- Create: `src/KoalaBooks.Web/Models/Api/CreateCustomerInvoiceRequest.cs` (includes `CreateCustomerInvoiceLineRequest`)
- Create: `src/KoalaBooks.Web/Models/Api/PostCustomerInvoiceRequest.cs`
- Create: `src/KoalaBooks.Web/Models/Api/MarkCustomerInvoicePaidRequest.cs`
- Create: `src/KoalaBooks.Web/Controllers/Api/CustomerInvoicesController.cs`
- Modify: `src/KoalaBooks.Web/Program.cs` (delete the old cookie-authed `/customer-invoices/{id}/pdf` route)
- Test: `tests/KoalaBooks.Tests/Api/CustomerInvoicesApiTests.cs`

**Interfaces:**
- Consumes: `ICustomerInvoiceService` (`GetAllAsync`, `GetByIdAsync`, `CreateAsync`, `PostAsync`, `MarkAsPaidAsync`, `FindMatchingBankTransactionsAsync`, `DeleteAsync`, `GetPdfAsync` from Task 3), `IFiscalYearService.GetByIdAsync`. Reuses `BankTransactionResponse` (already exists in `KoalaBooks.Web/Models/Api/BankTransactionResponse.cs`) for `find-matching-bank-tx`.
- Produces: routes `GET api/v1/fiscal-years/{fiscalYearId}/customer-invoices`, `GET api/v1/customer-invoices/{id}`, `POST api/v1/fiscal-years/{fiscalYearId}/customer-invoices`, `POST api/v1/customer-invoices/{id}/post`, `POST api/v1/customer-invoices/{id}/mark-paid`, `GET api/v1/fiscal-years/{fiscalYearId}/customer-invoices/find-matching-bank-tx`, `DELETE api/v1/customer-invoices/{id}`, `GET api/v1/customer-invoices/{id}/pdf`.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/KoalaBooks.Tests/Api/CustomerInvoicesApiTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerInvoicesApiTests`
Expected: FAIL — compile error, no `CustomerInvoicesController`/DTOs yet.

- [ ] **Step 3: Create the response DTOs**

Create `src/KoalaBooks.Web/Models/Api/CustomerInvoiceResponse.cs`:

```csharp
namespace KoalaBooks.Web.Models.Api;

public record CustomerInvoiceResponse(
    int Id,
    int FiscalYearId,
    int? CustomerId,
    string CustomerName,
    string? CustomerOrgNumber,
    string? CustomerAddress,
    string? CustomerPostalCode,
    string? CustomerCity,
    int InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string? OurReference,
    string? YourReference,
    string? Notes,
    List<CustomerInvoiceLineResponse> Lines,
    decimal AmountExclVat,
    decimal VatAmount,
    decimal TotalAmount,
    bool IsPosted,
    bool IsPaid,
    DateOnly? PaidDate,
    int? JournalEntryId,
    int? PaymentJournalEntryId,
    DateTime CreatedAt);

public record CustomerInvoiceLineResponse(
    int Id,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    int VatRate,
    decimal AmountExclVat,
    decimal VatAmount,
    decimal TotalAmount);
```

- [ ] **Step 4: Create the request DTOs**

Create `src/KoalaBooks.Web/Models/Api/CreateCustomerInvoiceRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class CreateCustomerInvoiceRequest
{
    public int? CustomerId { get; init; }

    [Required]
    public string CustomerName { get; init; } = "";

    [Required]
    public DateOnly? InvoiceDate { get; init; }

    [Required]
    public DateOnly? DueDate { get; init; }

    public string? OurReference { get; init; }
    public string? YourReference { get; init; }
    public string? Notes { get; init; }

    [MinLength(1)]
    public List<CreateCustomerInvoiceLineRequest> Lines { get; init; } = [];
}

public class CreateCustomerInvoiceLineRequest
{
    [Required]
    public string Description { get; init; } = "";

    public decimal Quantity { get; init; } = 1;
    public decimal UnitPrice { get; init; }
    public int VatRate { get; init; }
}
```

Create `src/KoalaBooks.Web/Models/Api/PostCustomerInvoiceRequest.cs`:

```csharp
namespace KoalaBooks.Web.Models.Api;

public class PostCustomerInvoiceRequest
{
    public int ReceivableAccountId { get; init; }
    public int RevenueAccountId { get; init; }
    public Dictionary<int, int> VatRateAccountIds { get; init; } = new();
}
```

Create `src/KoalaBooks.Web/Models/Api/MarkCustomerInvoicePaidRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class MarkCustomerInvoicePaidRequest
{
    [Required]
    public DateOnly? PaidDate { get; init; }

    public int BankAccountId { get; init; }
    public int ReceivableAccountId { get; init; }
    public int? LinkBankTransactionId { get; init; }
}
```

- [ ] **Step 5: Create the controller**

Create `src/KoalaBooks.Web/Controllers/Api/CustomerInvoicesController.cs`:

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class CustomerInvoicesController : ControllerBase
{
    private readonly ICustomerInvoiceService _customerInvoiceService;
    private readonly IFiscalYearService _fiscalYearService;

    public CustomerInvoicesController(
        ICustomerInvoiceService customerInvoiceService, IFiscalYearService fiscalYearService)
    {
        _customerInvoiceService = customerInvoiceService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/customer-invoices")]
    [ProducesResponseType<PagedResult<CustomerInvoiceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _customerInvoiceService.GetAllAsync(fiscalYearId);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapInvoice).ToList();

        return Ok(new PagedResult<CustomerInvoiceResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }

    [HttpGet("customer-invoices/{id:int}")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var invoice = await _customerInvoiceService.GetByIdAsync(id);
        if (invoice is null) return NotFound();
        return Ok(MapInvoice(invoice));
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/customer-invoices")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(int fiscalYearId, [FromBody] CreateCustomerInvoiceRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var invoice = new CustomerInvoice
        {
            FiscalYearId = fiscalYearId,
            CustomerId = request.CustomerId,
            CustomerName = request.CustomerName,
            InvoiceDate = request.InvoiceDate!.Value,
            DueDate = request.DueDate!.Value,
            OurReference = request.OurReference,
            YourReference = request.YourReference,
            Notes = request.Notes
        };
        var lines = request.Lines.Select(l => new CustomerInvoiceLine
        {
            Description = l.Description,
            Quantity = l.Quantity,
            UnitPrice = l.UnitPrice,
            VatRate = l.VatRate
        }).ToList();

        var (created, error) = await _customerInvoiceService.CreateAsync(invoice, lines);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapInvoice(created));
    }

    [HttpPost("customer-invoices/{id:int}/post")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Post(int id, [FromBody] PostCustomerInvoiceRequest request)
    {
        var existing = await _customerInvoiceService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var (posted, error) = await _customerInvoiceService.PostAsync(
            id, request.ReceivableAccountId, request.RevenueAccountId, request.VatRateAccountIds);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapInvoice(posted!));
    }

    [HttpPost("customer-invoices/{id:int}/mark-paid")]
    [ProducesResponseType<CustomerInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkPaid(int id, [FromBody] MarkCustomerInvoicePaidRequest request)
    {
        var existing = await _customerInvoiceService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var (paid, error) = await _customerInvoiceService.MarkAsPaidAsync(
            id, request.PaidDate!.Value, request.BankAccountId, request.ReceivableAccountId, request.LinkBankTransactionId);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapInvoice(paid!));
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/customer-invoices/find-matching-bank-tx")]
    [ProducesResponseType<List<BankTransactionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FindMatchingBankTransactions(
        int fiscalYearId, [FromQuery] decimal invoiceTotal, [FromQuery] DateOnly invoiceDate, [FromQuery] DateOnly dueDate)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var matches = await _customerInvoiceService.FindMatchingBankTransactionsAsync(
            fiscalYearId, invoiceTotal, invoiceDate, dueDate);

        return Ok(matches.Select(b => new BankTransactionResponse(
            b.Id, b.AccountId, b.Account.AccountNumber, b.Date, b.Amount, b.Description, b.Reference, b.Status, b.JournalEntryId)).ToList());
    }

    [HttpDelete("customer-invoices/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var existing = await _customerInvoiceService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var error = await _customerInvoiceService.DeleteAsync(id);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    [HttpGet("customer-invoices/{id:int}/pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPdf(int id)
    {
        var bytes = await _customerInvoiceService.GetPdfAsync(id);
        if (bytes is null) return NotFound();
        return File(bytes, "application/pdf");
    }

    private static CustomerInvoiceResponse MapInvoice(CustomerInvoice i) => new(
        i.Id, i.FiscalYearId, i.CustomerId, i.CustomerName, i.CustomerOrgNumber, i.CustomerAddress,
        i.CustomerPostalCode, i.CustomerCity, i.InvoiceNumber, i.InvoiceDate, i.DueDate,
        i.OurReference, i.YourReference, i.Notes,
        i.Lines.Select(l => new CustomerInvoiceLineResponse(
            l.Id, l.Description, l.Quantity, l.UnitPrice, l.VatRate, l.AmountExclVat, l.VatAmount, l.TotalAmount)).ToList(),
        i.AmountExclVat, i.VatAmount, i.TotalAmount, i.IsPosted, i.IsPaid, i.PaidDate,
        i.JournalEntryId, i.PaymentJournalEntryId, i.CreatedAt);
}
```

- [ ] **Step 6: Delete the old cookie-authed PDF route**

In `src/KoalaBooks.Web/Program.cs`, delete these lines (the block added/touched in Task 1 Step 5):

```csharp
app.MapGet("/customer-invoices/{id:int}/pdf", async (int id, ICustomerInvoiceService svc) =>
{
    var invoice = await svc.GetByIdAsync(id);
    if (invoice is null) return Results.NotFound();
    var bytes = KoalaBooks.Application.Services.CustomerInvoicePdfGenerator.Generate(invoice);
    var filename = $"Faktura-{invoice.InvoiceNumber}.pdf";
    return Results.File(bytes, "application/pdf", filename);
}).RequireAuthorization();
```

Leave the `/documents/{id}` route above it untouched (documented as out of scope in the spec) — it isn't immediately adjacent; a Hangfire-dashboard registration block sits between the two routes in `Program.cs`.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter CustomerInvoicesApiTests`
Expected: PASS (all tests)

- [ ] **Step 8: Run the full test suite**

Run: `dotnet build && dotnet test tests/KoalaBooks.Tests`
Expected: build succeeds, all tests pass (confirms deleting the minimal-API route didn't break anything else, e.g. no other test hit `/customer-invoices/{id}/pdf` directly — `CustomerInvoices.razor` itself is updated in Task 11).

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Web/Models/Api/CustomerInvoiceResponse.cs src/KoalaBooks.Web/Models/Api/CreateCustomerInvoiceRequest.cs src/KoalaBooks.Web/Models/Api/PostCustomerInvoiceRequest.cs src/KoalaBooks.Web/Models/Api/MarkCustomerInvoicePaidRequest.cs src/KoalaBooks.Web/Controllers/Api/CustomerInvoicesController.cs src/KoalaBooks.Web/Program.cs tests/KoalaBooks.Tests/Api/CustomerInvoicesApiTests.cs
git commit -m "$(cat <<'EOF'
Add CustomerInvoicesController REST endpoints (#122 Agent E + #290)

list/by-id/create/post/mark-paid/find-matching-bank-tx/delete/pdf,
bearer-authed. The pdf action closes #290's gap directly; it also
retires the old cookie-authed /customer-invoices/{id}/pdf minimal-API
route, which was the only API-shaped endpoint in the app not using
OpenIddict bearer auth.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `SieController` (export only)

**Files:**
- Create: `src/KoalaBooks.Web/Controllers/Api/SieController.cs`
- Test: `tests/KoalaBooks.Tests/Api/SieExportApiTests.cs`

**Interfaces:**
- Consumes: `ISieExportService.ExportAsync(int fiscalYearId, string? companyName)` (already exists, unchanged), `IFiscalYearService.GetByIdAsync`.
- Produces: route `GET api/v1/fiscal-years/{fiscalYearId}/sie-export?companyName=`.

- [ ] **Step 1: Write the failing integration tests**

Create `tests/KoalaBooks.Tests/Api/SieExportApiTests.cs`:

```csharp
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

// Covers SieController's export endpoint, closing #290's SIE-export gap.
// SIE import stays out of scope (Agent H's separate Hangfire-backed stream).
public class SieExportApiTests : IAsyncLifetime
{
    private WebApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _dbName = "";
    private int _fiscalYearId;

    private const string TestEmail = "api-test-sie-export@koalabooks.test";
    private const string TestPassword = "ApiTest123!";

    public async Task InitializeAsync()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;
        _factory = new WebApiFactory(connStr);
        _client = _factory.CreateClient();
        _fiscalYearId = await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    private async Task<int> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var org = new Organisation { Name = "API Test Org (SIE)", Slug = "api-test-sie-export", LegalForm = LegalForm.Aktiebolag };
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
            OrganisationId = org.Id, Name = "2026",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 12, 31)
        };
        db.FiscalYears.Add(fy);
        await db.SaveChangesAsync();

        return fy.Id;
    }

    private async Task<int> SeedOtherTenantFiscalYearAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org2 = new Organisation { Name = "Other Org", Slug = "other-org-sie-export", LegalForm = LegalForm.Aktiebolag };
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
    public async Task Export_ReturnsNonEmptyBytes()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/sie-export?companyName=Acme%20AB");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task Export_WithoutCompanyName_StillSucceeds()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/sie-export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Export_UnknownFiscalYear_Returns404()
    {
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/fiscal-years/999999/sie-export");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_CrossTenant_Returns404()
    {
        var otherFyId = await SeedOtherTenantFiscalYearAsync();
        var client = await AuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/v1/fiscal-years/{otherFyId}/sie-export");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Export_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync($"/api/v1/fiscal-years/{_fiscalYearId}/sie-export");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/KoalaBooks.Tests --filter SieExportApiTests`
Expected: FAIL — compile error / 404, no `SieController` yet.

- [ ] **Step 3: Create the controller**

Create `src/KoalaBooks.Web/Controllers/Api/SieController.cs`:

```csharp
using KoalaBooks.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class SieController : ControllerBase
{
    private readonly ISieExportService _sieExportService;
    private readonly IFiscalYearService _fiscalYearService;

    public SieController(ISieExportService sieExportService, IFiscalYearService fiscalYearService)
    {
        _sieExportService = sieExportService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/sie-export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Export(int fiscalYearId, [FromQuery] string? companyName = null)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var bytes = await _sieExportService.ExportAsync(fiscalYearId, companyName);
        return File(bytes, "application/octet-stream");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter SieExportApiTests`
Expected: PASS (all tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Web/Controllers/Api/SieController.cs tests/KoalaBooks.Tests/Api/SieExportApiTests.cs
git commit -m "$(cat <<'EOF'
Add bearer-authed SIE export REST endpoint (#290)

Closes the SIE-export half of #290's download-plumbing gap. Just the
export action — SIE import stays with #122's Agent H Hangfire-backed
stream, untouched here.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Stream-based download JS helper

**Files:**
- Modify: `src/KoalaBooks.Web/wwwroot/js/download.js`

**Interfaces:**
- Produces: `window.koala.downloadFileFromStream(streamRef, fileName, contentType, openInNewTab) : Promise<void>` — `streamRef` is a Blazor `DotNetStreamReference` passed from C#, exposing `.arrayBuffer()` in JS. Tasks 9-11 call this from `SieExport.razor`, `VatReport.razor`, `CustomerInvoices.razor`.

This file has no automated test today (no JS test infra in the repo) and this task doesn't add one — the same manual/Playwright verification approach used for other frontend-only changes applies; Task 13 covers a real end-to-end check.

- [ ] **Step 1: Replace `downloadFileFromBase64` with the stream-based helper**

Edit `src/KoalaBooks.Web/wwwroot/js/download.js` — replace the whole file:

```javascript
window.koala = {
    focusId: (id) => document.getElementById(id)?.focus()
};

// Reads a Blazor DotNetStreamReference into a Blob and either triggers a file
// download (SIE export, VAT CSV) or opens it in a new tab (customer invoice
// PDF). Works identically whether the bytes came from an in-process Server
// call or a WASM-side REST fetch — the render mode is invisible from here.
window.koala.downloadFileFromStream = async function (streamRef, fileName, contentType, openInNewTab) {
    const buffer = await streamRef.arrayBuffer();
    const blob = new Blob([buffer], { type: contentType });
    const url = URL.createObjectURL(blob);

    if (openInNewTab) {
        window.open(url, '_blank');
    } else {
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    }

    // Delay revocation so window.open's new tab (and slower browsers'
    // download handling) have time to actually read the blob URL.
    setTimeout(() => URL.revokeObjectURL(url), 30000);
};
```

- [ ] **Step 2: Commit**

```bash
git add src/KoalaBooks.Web/wwwroot/js/download.js
git commit -m "$(cat <<'EOF'
Replace base64 download interop with stream-based Blob helper

DotNetStreamReference works identically under Server (tunneled over
the circuit) and WASM (direct in-browser interop) rendering, and
avoids the ~33% size penalty of base64-encoding bytes across the
old JS interop call. Closes the JS-interop half of #290's decision
list.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: `KoalaBooks.Application.csproj` — no separate task

(Folded into Task 1, Step 4 — listed here only so the file list in Task 1 is discoverable. No action needed.)

---

## Task 9: Wire `SieExport.razor` to the stream-based helper

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/SieExport.razor`

**Interfaces:**
- Consumes: `window.koala.downloadFileFromStream` (Task 7), `ISieExportService.ExportAsync` (unchanged, already exists).

- [ ] **Step 1: Update `ExportAsync` to use the stream helper**

In `src/KoalaBooks.Components/Pages/SieExport.razor`, replace the `ExportAsync` method:

```csharp
    private async Task ExportAsync()
    {
        _exporting = true;

        try
        {
            var bytes = await SieExportService.ExportAsync(_selectedFyId, string.IsNullOrWhiteSpace(_companyName) ? null : _companyName);
            var fy = _fiscalYears!.First(f => f.Id == _selectedFyId);
            var fileName = $"koalabooks_{fy.Name}.se";

            using var stream = new MemoryStream(bytes);
            using var streamRef = new DotNetStreamReference(stream);
            await JS.InvokeVoidAsync("koala.downloadFileFromStream", streamRef, fileName, "application/octet-stream", false);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Export misslyckades: {ex.Message}", Severity.Error);
        }
        finally
        {
            _exporting = false;
        }
    }
```

Add `@using System.IO` to the top of the file (alongside the existing `@using` directives), matching the convention already used in `VatReport.razor` for the same `MemoryStream` need:

```razor
@page "/export/sie"
@using System.IO
@using KoalaBooks.Domain.Interfaces
```

`Microsoft.JSInterop` (for `DotNetStreamReference`) is already globally imported via `KoalaBooks.Components/_Imports.razor` — no extra using needed for that.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Manual verification**

Run the app (`dotnet run --project src/KoalaBooks.Web` or via the `run` skill), log in, go to `/export/sie`, select a fiscal year, click "Exportera SIE-4". Confirm a `.se` file downloads with non-zero size. Check the browser console for JS errors.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/SieExport.razor
git commit -m "$(cat <<'EOF'
Switch SieExport.razor to the stream-based download helper

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Wire `VatReport.razor` to the stream-based helper

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/VatReport.razor`

**Interfaces:**
- Consumes: `window.koala.downloadFileFromStream` (Task 7), `IVatReportCsvExporter.Build` (unchanged, already exists, pure/no-DB).

- [ ] **Step 1: Update `ExportCsv` to use the stream helper**

In `src/KoalaBooks.Components/Pages/VatReport.razor`, replace the `ExportCsv` method:

```csharp
    private async Task ExportCsv()
    {
        if (_data is null) return;
        var fyName = string.Concat((SelectedFy?.Name ?? "").Split(Path.GetInvalidFileNameChars()));
        var bytes = CsvExporter.Build(_data, fyName, FromDate, ToDate);
        var period = (FromDate, ToDate) switch
        {
            ({ } f, { } t) => $"_{f:yyyyMMdd}-{t:yyyyMMdd}",
            ({ } f, null) => $"_from-{f:yyyyMMdd}",
            (null, { } t) => $"_to-{t:yyyyMMdd}",
            _ => ""
        };
        var fileName = $"momsredovisning_{fyName}{period}.csv";

        using var stream = new MemoryStream(bytes);
        using var streamRef = new DotNetStreamReference(stream);
        await JS.InvokeVoidAsync("koala.downloadFileFromStream", streamRef, fileName, "text/csv", false);
    }
```

(`@using System.IO` is already present in this file for `Path.GetInvalidFileNameChars()`; no import changes needed. `window.print` in `PrintReport` is untouched.)

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 3: Manual verification**

Run the app, go to `/reports/vat`, select a fiscal year and generate a report, click "Exportera CSV". Confirm a `.csv` file downloads with the expected filename pattern and non-zero size.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Components/Pages/VatReport.razor
git commit -m "$(cat <<'EOF'
Switch VatReport.razor CSV export to the stream-based download helper

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Wire `CustomerInvoices.razor`'s PDF link to the stream-based helper

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`

**Interfaces:**
- Consumes: `window.koala.downloadFileFromStream` (Task 7), `ICustomerInvoiceService.GetPdfAsync` (Task 3).

- [ ] **Step 1: Replace the `<a href>` PDF link with a button**

In `src/KoalaBooks.Components/Pages/CustomerInvoices.razor`, replace:

```razor
                            <a href="/customer-invoices/@inv.Id/pdf" target="_blank"
                               class="btn btn-sm btn-secondary" title="Ladda ner PDF">PDF</a>
```

with:

```razor
                            <button class="btn btn-sm btn-secondary" title="Visa PDF" @onclick="() => DownloadPdfAsync(inv)">PDF</button>
```

- [ ] **Step 2: Add the `DownloadPdfAsync` method**

In the `@code` block, add near `DeleteInvoice`:

```csharp
    private async Task DownloadPdfAsync(CustomerInvoice inv)
    {
        var bytes = await InvoiceService.GetPdfAsync(inv.Id);
        if (bytes is null) { _error = "Fakturan hittades inte."; return; }

        using var stream = new MemoryStream(bytes);
        using var streamRef = new DotNetStreamReference(stream);
        await JS.InvokeVoidAsync("koala.downloadFileFromStream", streamRef, $"Faktura-{inv.InvoiceNumber}.pdf", "application/pdf", true);
    }
```

Add `@using System.IO` to the top of the file, alongside the existing `@using` directives:

```razor
@page "/customer-invoices"
@using System.IO
@using KoalaBooks.Domain
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 4: Manual verification**

Run the app, go to `/customer-invoices`, click "PDF" on an existing invoice. Confirm a new tab opens showing the PDF (same visual result as before, just via a Blob URL now instead of a direct link).

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Components/Pages/CustomerInvoices.razor
git commit -m "$(cat <<'EOF'
Switch CustomerInvoices.razor PDF link to the stream-based helper

Replaces the plain <a href> to the now-deleted cookie-authed minimal-
API route with a button that fetches bytes via
ICustomerInvoiceService.GetPdfAsync and opens them through the new
Blob-based JS helper, keeping the "open in a new tab" UX.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: WASM-side client implementations

**Files:**
- Create: `src/KoalaBooks.Client/Services/CustomerApiService.cs`
- Create: `src/KoalaBooks.Client/Services/CustomerInvoiceApiService.cs`
- Create: `src/KoalaBooks.Client/Services/SieExportApiService.cs`
- Modify: `src/KoalaBooks.Client/Program.cs`

**Interfaces:**
- Consumes: the `"KoalaBooks.Api"` named `HttpClient` (already registered, bearer-authed via `CookieBridgeTokenHandler`), `ApiJson.Options`, all the REST endpoints from Tasks 4-6.
- Produces: `CustomerApiService : ICustomerService`, `CustomerInvoiceApiService : ICustomerInvoiceService`, `SieExportApiService : ISieExportService` — registered in `KoalaBooks.Client/Program.cs`, ready for any future page that flips to `InteractiveAuto` and injects these interfaces (none does yet, per this plan's scope).

This task has no new automated tests: these classes aren't exercised by any WASM-rendered page yet (per the spec's decision not to flip render modes in this PR), so there's no integration point to test against, matching how the existing `SupplierInvoiceApiService`/`BankImportApiService` (also written ahead of any consuming WASM page) have none either. Correctness here is "compiles, and each method either calls the right endpoint or throws `NotSupportedException`" — verified by Step 2's build.

- [ ] **Step 1: Create `CustomerApiService`**

Create `src/KoalaBooks.Client/Services/CustomerApiService.cs`:

```csharp
using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

public class CustomerApiService(HttpClient http) : ICustomerService
{
    public async Task<List<Customer>> GetAllAsync(int organisationId)
    {
        // organisationId is resolved server-side from the bearer token's tenant claim,
        // same as CustomersController — the parameter is kept only to satisfy the shared
        // interface (Server's CustomerService still uses it for the direct EF query).
        var result = await http.GetFromJsonAsync<List<Customer>>("api/v1/customers", ApiJson.Options).ConfigureAwait(false);
        return result ?? [];
    }

    public async Task<Customer?> GetByIdAsync(int id) =>
        await http.GetFromJsonAsync<Customer>($"api/v1/customers/{id}", ApiJson.Options).ConfigureAwait(false);

    public async Task<(Customer? Customer, string? Error)> CreateAsync(Customer customer)
    {
        var response = await http.PostAsJsonAsync("api/v1/customers", customer, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var created = await response.Content.ReadFromJsonAsync<Customer>(ApiJson.Options).ConfigureAwait(false);
        return (created, null);
    }

    public async Task<(Customer? Customer, string? Error)> UpdateAsync(Customer customer)
    {
        var response = await http.PutAsJsonAsync($"api/v1/customers/{customer.Id}", customer, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var updated = await response.Content.ReadFromJsonAsync<Customer>(ApiJson.Options).ConfigureAwait(false);
        return (updated, null);
    }

    public async Task<string?> DeactivateAsync(int customerId)
    {
        var response = await http.PostAsync($"api/v1/customers/{customerId}/deactivate", null).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? null : await ApiJson.ReadErrorAsync(response).ConfigureAwait(false);
    }
}
```

- [ ] **Step 2: Create `CustomerInvoiceApiService`**

Create `src/KoalaBooks.Client/Services/CustomerInvoiceApiService.cs`:

```csharp
using System.Net.Http.Json;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

public class CustomerInvoiceApiService(HttpClient http) : ICustomerInvoiceService
{
    public async Task<List<CustomerInvoice>> GetAllAsync(int fiscalYearId)
    {
        var result = await http.GetFromJsonAsync<PagedResult>(
            $"api/v1/fiscal-years/{fiscalYearId}/customer-invoices?pageSize=200", ApiJson.Options).ConfigureAwait(false);
        return result?.Items ?? [];
    }

    public async Task<CustomerInvoice?> GetByIdAsync(int id) =>
        await http.GetFromJsonAsync<CustomerInvoice>($"api/v1/customer-invoices/{id}", ApiJson.Options).ConfigureAwait(false);

    public async Task<byte[]?> GetPdfAsync(int id)
    {
        var response = await http.GetAsync($"api/v1/customer-invoices/{id}/pdf").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    public async Task<(CustomerInvoice? Invoice, string? Error)> CreateAsync(
        CustomerInvoice invoice, List<CustomerInvoiceLine> lines)
    {
        var payload = new
        {
            invoice.CustomerId,
            invoice.CustomerName,
            invoice.InvoiceDate,
            invoice.DueDate,
            invoice.OurReference,
            invoice.YourReference,
            invoice.Notes,
            Lines = lines.Select(l => new { l.Description, l.Quantity, l.UnitPrice, l.VatRate })
        };
        var response = await http.PostAsJsonAsync(
            $"api/v1/fiscal-years/{invoice.FiscalYearId}/customer-invoices", payload, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var created = await response.Content.ReadFromJsonAsync<CustomerInvoice>(ApiJson.Options).ConfigureAwait(false);
        return (created, null);
    }

    public async Task<(CustomerInvoice? Invoice, string? Error)> PostAsync(
        int invoiceId, int receivableAccountId, int revenueAccountId, IReadOnlyDictionary<int, int> vatRateAccountIds)
    {
        var payload = new { ReceivableAccountId = receivableAccountId, RevenueAccountId = revenueAccountId, VatRateAccountIds = vatRateAccountIds };
        var response = await http.PostAsJsonAsync($"api/v1/customer-invoices/{invoiceId}/post", payload, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var posted = await response.Content.ReadFromJsonAsync<CustomerInvoice>(ApiJson.Options).ConfigureAwait(false);
        return (posted, null);
    }

    public async Task<List<BankTransaction>> FindMatchingBankTransactionsAsync(
        int fiscalYearId, decimal invoiceTotal, DateOnly invoiceDate, DateOnly dueDate)
    {
        var url = $"api/v1/fiscal-years/{fiscalYearId}/customer-invoices/find-matching-bank-tx" +
                  $"?invoiceTotal={invoiceTotal}&invoiceDate={invoiceDate:yyyy-MM-dd}&dueDate={dueDate:yyyy-MM-dd}";
        var result = await http.GetFromJsonAsync<List<BankTransactionMatchDto>>(url, ApiJson.Options).ConfigureAwait(false);

        // The endpoint returns BankTransactionResponse shape (flat AccountNumber string,
        // no Account nav, no OrganisationId/ImportedAt) — deserializing straight into
        // BankTransaction would silently leave the required Account nav null. Map by hand
        // instead; Account.Name isn't in the DTO so it's approximated from the number.
        return (result ?? []).Select(b => new BankTransaction
        {
            Id = b.Id,
            AccountId = b.AccountId,
            Account = new Account { AccountNumber = b.AccountNumber, Name = b.AccountNumber },
            Date = b.Date,
            Amount = b.Amount,
            Description = b.Description,
            Reference = b.Reference,
            Status = b.Status,
            JournalEntryId = b.JournalEntryId
        }).ToList();
    }

    private record BankTransactionMatchDto(
        int Id, int AccountId, string AccountNumber, DateOnly Date, decimal Amount,
        string Description, string? Reference, BankTransactionStatus Status, int? JournalEntryId);

    public async Task<(CustomerInvoice? Invoice, string? Error)> MarkAsPaidAsync(
        int invoiceId, DateOnly paidDate, int bankAccountId, int receivableAccountId, int? linkBankTransactionId = null)
    {
        var payload = new
        {
            PaidDate = paidDate,
            BankAccountId = bankAccountId,
            ReceivableAccountId = receivableAccountId,
            LinkBankTransactionId = linkBankTransactionId
        };
        var response = await http.PostAsJsonAsync($"api/v1/customer-invoices/{invoiceId}/mark-paid", payload, ApiJson.Options).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, await ApiJson.ReadErrorAsync(response).ConfigureAwait(false));

        var paid = await response.Content.ReadFromJsonAsync<CustomerInvoice>(ApiJson.Options).ConfigureAwait(false);
        return (paid, null);
    }

    public async Task<string?> DeleteAsync(int invoiceId)
    {
        var response = await http.DeleteAsync($"api/v1/customer-invoices/{invoiceId}").ConfigureAwait(false);
        return response.IsSuccessStatusCode ? null : await ApiJson.ReadErrorAsync(response).ConfigureAwait(false);
    }

    public Task<Account?> FindAccountByPrefixAsync(int fiscalYearId, string prefix) =>
        Task.FromException<Account?>(
            new NotSupportedException("Finding an account by prefix has no REST endpoint yet."));

    private record PagedResult(List<CustomerInvoice> Items, int Page, int PageSize, int TotalCount);
}
```

- [ ] **Step 3: Create `SieExportApiService`**

Create `src/KoalaBooks.Client/Services/SieExportApiService.cs`:

```csharp
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Client.Services;

public class SieExportApiService(HttpClient http) : ISieExportService
{
    public async Task<byte[]> ExportAsync(int fiscalYearId, string? companyName = null)
    {
        var url = $"api/v1/fiscal-years/{fiscalYearId}/sie-export";
        if (!string.IsNullOrWhiteSpace(companyName))
            url += $"?companyName={Uri.EscapeDataString(companyName)}";

        var response = await http.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Register the new services and the pure CSV exporter in `KoalaBooks.Client/Program.cs`**

In `src/KoalaBooks.Client/Program.cs`, add the missing `using` and extend the PoC-scope registration block:

```csharp
using KoalaBooks.Application.Services;
```

```csharp
// PoC scope: only the services the WASM-rendered /review page needs.
builder.Services.AddScoped<IFiscalYearService, FiscalYearApiService>();
builder.Services.AddScoped<IAccountService, AccountApiService>();
builder.Services.AddScoped<IJournalEntryService, JournalEntryApiService>();
// MainLayout's nav badge counts resolve these via ScopeFactory regardless of which page
// is rendering, so they're needed even though no WASM page injects them directly.
builder.Services.AddScoped<IBankImportService, BankImportApiService>();
builder.Services.AddScoped<ISupplierInvoiceService, SupplierInvoiceApiService>();
// #290: no WASM page injects these yet (render-mode flips are a separate follow-up),
// but the REST endpoints they depend on now exist, so registering them here keeps
// the DI-swap pattern consistent and ready for whenever a page needs them.
builder.Services.AddScoped<ICustomerService, CustomerApiService>();
builder.Services.AddScoped<ICustomerInvoiceService, CustomerInvoiceApiService>();
builder.Services.AddScoped<ISieExportService, SieExportApiService>();
// Pure function, no DB — the same concrete type Server uses, not an HTTP-backed variant.
builder.Services.AddSingleton<IVatReportCsvExporter, VatReportCsvExporter>();
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build`
Expected: build succeeds (`KoalaBooks.Client` targets WASM — this also confirms nothing in the new files pulls in a non-trimmable/non-WASM-compatible dependency).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests pass (no behavior change to anything under test — this task only adds new, currently-unused classes).

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Client/Services/CustomerApiService.cs src/KoalaBooks.Client/Services/CustomerInvoiceApiService.cs src/KoalaBooks.Client/Services/SieExportApiService.cs src/KoalaBooks.Client/Program.cs
git commit -m "$(cat <<'EOF'
Add WASM-side ICustomerService/ICustomerInvoiceService/ISieExportService

HTTP-backed implementations following the existing AccountApiService/
SupplierInvoiceApiService DI-swap pattern, calling the REST endpoints
added in this branch. IVatReportCsvExporter needs no API variant —
it's pure, so the real implementation is registered directly. No page
injects these yet; that's a separate future render-mode-flip PR.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: Final verification

**Files:** none (verification only)

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: 0 errors, 0 warnings (repo uses a curated analyzer rule set — see `feedback_editorconfig_analyzers_168` — treat any new warning as something to fix, not suppress).

- [ ] **Step 2: Full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests pass, including every test added in Tasks 1-6.

- [ ] **Step 3: Manual end-to-end pass**

Using the `run` skill (or `dotnet run --project src/KoalaBooks.Web` directly), log in and exercise, in order:
1. `/export/sie` — export a SIE file, confirm download.
2. `/reports/vat` — generate a report, export CSV, confirm download; click "Skriv ut" and confirm `window.print()` still opens the print dialog (untouched by this plan, but worth confirming nothing regressed).
3. `/customer-invoices` — click "PDF" on an invoice, confirm it opens in a new tab.
4. Check the browser console across all three for JS errors.
5. Hit `GET /api/v1/customers`, `GET /api/v1/customer-invoices/{id}/pdf`, and `GET /api/v1/fiscal-years/{id}/sie-export` directly with a bearer token (e.g. via `curl`, reusing the `/connect/token` password-grant flow from the integration tests) to confirm they 401 without a token and succeed with one — a second, direct confirmation beyond what the integration tests already assert.

- [ ] **Step 4: Report status**

No commit for this task (verification only). If any step fails, return to the relevant earlier task and fix before proceeding to PR.
