# REST API v2 — Supplier Invoices & Bank Transactions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close out issue #121 (REST API v2: supplier invoices read/write, bank transactions read) by first resolving its recommended prerequisite, issue #120 (register a dedicated OpenIddict API client to replace `AcceptAnonymousClients()`).

**Architecture:** Two new API controllers (`SupplierInvoicesController`, `BankTransactionsController`) in `KoalaBooks.Web/Controllers/Api/`, following the exact structural pattern of the existing `JournalEntriesController`/`AccountsController`: `[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]`, manual DTO mapping, in-memory pagination via a shared `PagedResult<T>`, tuple-returning Application services for mutations. Supplier invoice endpoints reuse and extend the existing `ISupplierInvoiceService`. Bank transaction read endpoints extend the existing `IBankImportService` (which — despite living in `KoalaBooks.Domain.Interfaces` rather than `Application.Services`, an existing layering quirk — is the service the issue's "reuse existing Application services" note points to, since it's the only place bank-transaction queries currently live). Before any v2 endpoint work, issue #120 seeds a `koalabooks-api` public OpenIddict client and removes the `AcceptAnonymousClients()` escape hatch, so every subsequent integration test authenticates the same way a real API caller would.

**Tech Stack:** .NET 8, ASP.NET Core Web API, EF Core / Npgsql, OpenIddict (server + validation), xUnit + Testcontainers Postgres.

## Global Constraints

- All new/modified API endpoints require `[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]` — copy this exactly from `JournalEntriesController`.
- No AutoMapper anywhere in this codebase — all DTO mapping is a private static method on the controller, exactly like `JournalEntriesController.MapEntry`.
- Response DTOs are positional `record`s; request DTOs are `class`es with `init`-only properties and `System.ComponentModel.DataAnnotations` validation attributes. Namespace `KoalaBooks.Web.Models.Api`, one type per file, directory `src/KoalaBooks.Web/Models/Api/`.
- Enums on response DTOs use `[property: JsonConverter(typeof(JsonStringEnumConverter))]`.
- Pagination is in-memory: load the full filtered set from the service, then `Skip/Take` in the controller, `pageSize` clamped to `[1, 200]`, `page` clamped to `≥ 1`. Wrap in the existing generic `PagedResult<T>` (`src/KoalaBooks.Web/Models/Api/PagedResult.cs`) — do not create a new paging type.
- All error returns from services are `(Entity? Result, string? Error)` tuples; controllers translate a non-null `Error` to `Problem(detail: error, statusCode: StatusCodes.Status400BadRequest)`, matching `JournalEntriesController.Create`/`Delete`.
- Every existing test in `tests/KoalaBooks.Tests/Api/ApiTests.cs` and `tests/KoalaBooks.Tests/` must keep passing after each task — run `dotnet test` from the repo root before every commit.
- Swedish user-facing error strings (e.g. `"Fakturan hittades inte."`) are the established convention in `SupplierInvoiceService`/`BankImportService` — match it for any new error message in those files.
- Money is `decimal`; dates are `DateOnly`; timestamps are `DateTime` (UTC, `DateTime.UtcNow`).

---

## File Structure

**New files:**
- `src/KoalaBooks.Infrastructure/Services/ApiClientSeeder.cs` — idempotent OpenIddict client seeder for `koalabooks-api` (issue #120), modeled on `AspireDashboardSeeder.cs`.
- `src/KoalaBooks.Web/Models/Api/SupplierInvoiceResponse.cs`
- `src/KoalaBooks.Web/Models/Api/CreateSupplierInvoiceRequest.cs`
- `src/KoalaBooks.Web/Models/Api/UpdateSupplierInvoiceRequest.cs`
- `src/KoalaBooks.Web/Models/Api/BankTransactionResponse.cs`
- `src/KoalaBooks.Web/Controllers/Api/SupplierInvoicesController.cs`
- `src/KoalaBooks.Web/Controllers/Api/BankTransactionsController.cs`
- `tests/KoalaBooks.Tests/SupplierInvoiceReadWriteTests.cs` — unit tests for the new `GetByIdAsync`/`UpdateAsync` service methods.
- `tests/KoalaBooks.Tests/BankTransactionQueryTests.cs` — unit tests for the new `IBankImportService` query methods.

**Modified files:**
- `src/KoalaBooks.Web/Program.cs` — remove `options.AcceptAnonymousClients()` (line 111); restructure the startup seeding block (lines 246–284) so `ApiClientSeeder.SeedAsync` runs in both the `Testing` and non-`Testing` branches.
- `src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs` / `SupplierInvoiceService.cs` — add `GetByIdAsync(int id)` and `UpdateAsync(SupplierInvoice invoice)`.
- `src/KoalaBooks.Domain/Interfaces/IBankImportService.cs` / `src/KoalaBooks.Infrastructure/Services/BankImportService.cs` — add `GetByFiscalYearAsync(int fiscalYearId, DateOnly? from, DateOnly? to, int? accountId)` and `GetByIdAsync(int id)`.
- `tests/KoalaBooks.Tests/Api/ApiTests.cs` — add `client_id` to `GetBearerTokenAsync()`; add one new auth test for #120; add `── Supplier invoice tests ──` and `── Bank transaction tests ──` sections for #121.
- `tests/KoalaBooks.Tests/TestFixture.cs` — expose `SupplierInvoiceService` and `BankImportService` properties, matching the existing `JournalEntryService`/`FiscalYearService` properties.

---

## Task 1: Register the `koalabooks-api` OpenIddict client, remove `AcceptAnonymousClients()` (closes #120)

**Files:**
- Create: `src/KoalaBooks.Infrastructure/Services/ApiClientSeeder.cs`
- Modify: `src/KoalaBooks.Web/Program.cs:111` (remove line), `src/KoalaBooks.Web/Program.cs:246-284` (restructure seeding block)
- Modify: `tests/KoalaBooks.Tests/Api/ApiTests.cs:103-114` (`GetBearerTokenAsync`), plus one new test

**Interfaces:**
- Produces: `KoalaBooks.Infrastructure.Services.ApiClientSeeder.SeedAsync(IServiceProvider services)` (static, idempotent — later tasks and any future client seeders don't need to touch this signature) and `ApiClientSeeder.ClientId` (`const string = "koalabooks-api"`).

- [ ] **Step 1: Write the failing test**

Add to `tests/KoalaBooks.Tests/Api/ApiTests.cs`, in the `── Auth tests ──` section (after `ConnectToken_ValidCredentials_ReturnsAccessTokenWithOrgId`, around line 149):

```csharp
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
```

This asserts the exact behavior issue #120 is about: today `AcceptAnonymousClients()` accepts any (or no) `client_id`, so this request currently succeeds with 200. Once the client registry is enforced, an unrecognized `client_id` must be rejected.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ApiTests.ConnectToken_UnregisteredClientId_ReturnsUnauthorized"`
Expected: FAIL — asserted `Unauthorized` but got `OK` (200), because `AcceptAnonymousClients()` is still active.

- [ ] **Step 3: Create the seeder**

Create `src/KoalaBooks.Infrastructure/Services/ApiClientSeeder.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace KoalaBooks.Infrastructure.Services;

// Public client for third-party/script REST API callers using the password grant.
// Registering a real client (instead of AcceptAnonymousClients()) lets us identify
// which application issued a token request and revoke it independently of user accounts.
public static class ApiClientSeeder
{
    public const string ClientId = "koalabooks-api";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        var logger = services.GetRequiredService<ILoggerFactory>()
                             .CreateLogger(typeof(ApiClientSeeder));

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = OpenIddictConstants.ClientTypes.Public,
            DisplayName = "KoalaBooks API",
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.Password,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.Scopes.Email,
                OpenIddictConstants.Permissions.Scopes.Profile,
                OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess,
            }
        };

        var existing = await manager.FindByClientIdAsync(ClientId).ConfigureAwait(false);
        if (existing is null)
        {
            await manager.CreateAsync(descriptor).ConfigureAwait(false);
            logger.LogInformation("Created OpenIddict client '{ClientId}'", ClientId);
        }
        else
        {
            await manager.UpdateAsync(existing, descriptor).ConfigureAwait(false);
            logger.LogInformation("Updated OpenIddict client '{ClientId}'", ClientId);
        }
    }
}
```

The scope permissions (`Email`, `Profile`, `Scope + OfflineAccess`) mirror `AspireDashboardSeeder`'s descriptor — `AllowRefreshTokenFlow()` is enabled server-wide, so the client needs the `offline_access` scope permission or refresh-token issuance will fail.

- [ ] **Step 4: Wire the seeder into `Program.cs` and remove `AcceptAnonymousClients()`**

In `src/KoalaBooks.Web/Program.cs`, delete line 111:

```csharp
options.AcceptAnonymousClients();
```

Then replace the startup seeding block (currently lines 246–284) with:

```csharp
// Auto-migrate and seed on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
    {
        db.Database.EnsureCreated();
    }
    else
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        if (!app.Environment.IsProduction() &&
            (app.Environment.IsDevelopment() || builder.Configuration["SEED_DEMO_DATA"] == "true"))
        {
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);
        }

        var dashboardRedirectUri = builder.Configuration["AspireDashboard:OidcRedirectUri"]
            ?? "http://localhost:18888/";
        var dashboardClientSecret = builder.Configuration["AspireDashboard:OidcClientSecret"]
            ?? "aspire-dashboard-dev-secret";
        await AspireDashboardSeeder.SeedAsync(scope.ServiceProvider, new Uri(dashboardRedirectUri), dashboardClientSecret);

        // Stopgap until there's a real UI to grant roles.
        await AdminRoleSeeder.SeedAsync(scope.ServiceProvider, builder.Configuration["AdminSeed:Email"]);
    }

    // Seeded in both Testing and non-Testing environments: every OpenIddict client (this one,
    // and any future browser/desktop client) must be registered here so WebApiFactory-based
    // integration tests and real deployments can request tokens now that
    // AcceptAnonymousClients() has been removed.
    await ApiClientSeeder.SeedAsync(scope.ServiceProvider);
}
```

Note: `ApiClientSeeder.SeedAsync` is called once, after the `if/else`, so it runs unconditionally — this is the one line to extend if a future client (e.g. a browser-hosted or desktop client) needs seeding too.

- [ ] **Step 5: Update the test token helper to pass `client_id`**

In `tests/KoalaBooks.Tests/Api/ApiTests.cs`, update `GetBearerTokenAsync` (lines 103–114):

```csharp
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
```

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS — all existing `ApiTests` (now using `client_id=koalabooks-api`) pass, and the new `ConnectToken_UnregisteredClientId_ReturnsUnauthorized` test passes because OpenIddict now rejects `not-a-real-client`.

- [ ] **Step 7: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/ApiClientSeeder.cs src/KoalaBooks.Web/Program.cs tests/KoalaBooks.Tests/Api/ApiTests.cs
git commit -m "feat: register dedicated OpenIddict API client, remove AcceptAnonymousClients (#120)"
```

---

## Task 2: `SupplierInvoiceService.GetByIdAsync` / `UpdateAsync`

**Files:**
- Modify: `src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs`
- Modify: `src/KoalaBooks.Application/Services/SupplierInvoiceService.cs`
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs` (expose `SupplierInvoiceService`)
- Create: `tests/KoalaBooks.Tests/SupplierInvoiceReadWriteTests.cs`

**Interfaces:**
- Consumes: `SupplierInvoice` entity (`src/KoalaBooks.Domain/Entities/SupplierInvoice.cs`) — fields `Id, FiscalYearId, SupplierName, InvoiceNumber, InvoiceDate, DueDate, AmountExclVat, VatAmount, TotalAmount, Notes, IsPaid, JournalEntryId`.
- Produces: `Task<SupplierInvoice?> GetByIdAsync(int id)`, `Task<(SupplierInvoice? Invoice, string? Error)> UpdateAsync(SupplierInvoice invoice)` — Task 4's controller consumes both by exact name.

- [ ] **Step 1: Expose `SupplierInvoiceService` on `TestFixture`**

In `tests/KoalaBooks.Tests/TestFixture.cs`, add a property next to `JournalEntryService` (line 19) and construct it next to line 50:

```csharp
public SupplierInvoiceService SupplierInvoiceService { get; }
```

```csharp
SupplierInvoiceService = new SupplierInvoiceService(Db);
```

- [ ] **Step 2: Write the failing tests**

Create `tests/KoalaBooks.Tests/SupplierInvoiceReadWriteTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class SupplierInvoiceReadWriteTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;

    public SupplierInvoiceReadWriteTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
    }

    public void Dispose() => _f.Dispose();

    private SupplierInvoice MakeInvoice(string supplier = "Acme AB", decimal total = 1000m) => new()
    {
        FiscalYearId = _fy.Id,
        SupplierName = supplier,
        InvoiceDate = new DateOnly(2026, 3, 1),
        DueDate = new DateOnly(2026, 3, 31),
        AmountExclVat = 800m,
        VatAmount = 200m,
        TotalAmount = total
    };

    [Fact]
    public async Task GetByIdAsync_ExistingInvoice_ReturnsIt()
    {
        var (created, error) = await _f.SupplierInvoiceService.CreateAsync(MakeInvoice());
        Assert.Null(error);
        Assert.NotNull(created);

        var found = await _f.SupplierInvoiceService.GetByIdAsync(created.Id);

        Assert.NotNull(found);
        Assert.Equal("Acme AB", found.SupplierName);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var found = await _f.SupplierInvoiceService.GetByIdAsync(999999);
        Assert.Null(found);
    }

    [Fact]
    public async Task UpdateAsync_DraftInvoice_UpdatesFields()
    {
        var (created, error) = await _f.SupplierInvoiceService.CreateAsync(MakeInvoice());
        Assert.Null(error);
        Assert.NotNull(created);

        var update = new SupplierInvoice
        {
            Id = created.Id,
            SupplierName = "Acme AB (updated)",
            InvoiceDate = new DateOnly(2026, 3, 2),
            DueDate = new DateOnly(2026, 4, 1),
            AmountExclVat = 900m,
            VatAmount = 225m,
            TotalAmount = 1125m
        };

        var (updated, updateError) = await _f.SupplierInvoiceService.UpdateAsync(update);

        Assert.Null(updateError);
        Assert.NotNull(updated);
        Assert.Equal("Acme AB (updated)", updated.SupplierName);
        Assert.Equal(1125m, updated.TotalAmount);
    }

    [Fact]
    public async Task UpdateAsync_PostedInvoice_ReturnsError()
    {
        var (created, _) = await _f.SupplierInvoiceService.CreateAsync(MakeInvoice());
        var (_, liability, _, _, expense) = _f.CreateStandardAccounts(_fy.Id);
        var (posted, postError) = await _f.SupplierInvoiceService.PostAsync(created!.Id, expense.Id, liability.Id, null);
        Assert.Null(postError);
        Assert.NotNull(posted);

        var update = new SupplierInvoice
        {
            Id = created.Id,
            SupplierName = "Should not apply",
            InvoiceDate = created.InvoiceDate,
            DueDate = created.DueDate,
            AmountExclVat = created.AmountExclVat,
            VatAmount = created.VatAmount,
            TotalAmount = created.TotalAmount
        };

        var (updated, error) = await _f.SupplierInvoiceService.UpdateAsync(update);

        Assert.Null(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsError()
    {
        var update = MakeInvoice();
        update.Id = 999999;

        var (updated, error) = await _f.SupplierInvoiceService.UpdateAsync(update);

        Assert.Null(updated);
        Assert.NotNull(error);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SupplierInvoiceReadWriteTests"`
Expected: FAIL to compile — `ISupplierInvoiceService`/`SupplierInvoiceService` have no `GetByIdAsync`/`UpdateAsync` yet.

- [ ] **Step 4: Add the interface members**

In `src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs`, add after `GetAllAsync`:

```csharp
Task<SupplierInvoice?> GetByIdAsync(int id);
Task<(SupplierInvoice? Invoice, string? Error)> UpdateAsync(SupplierInvoice invoice);
```

- [ ] **Step 5: Implement in `SupplierInvoiceService`**

In `src/KoalaBooks.Application/Services/SupplierInvoiceService.cs`, add after `GetAllAsync` (line 29):

```csharp
public async Task<SupplierInvoice?> GetByIdAsync(int id)
{
    return await _db.SupplierInvoices
        .Include(s => s.JournalEntry)
        .Include(s => s.PaymentJournalEntry)
        .FirstOrDefaultAsync(s => s.Id == id).ConfigureAwait(false);
}

public async Task<(SupplierInvoice? Invoice, string? Error)> UpdateAsync(SupplierInvoice invoice)
{
    if (string.IsNullOrWhiteSpace(invoice.SupplierName))
        return (null, "Leverantörsnamn är obligatoriskt.");
    if (invoice.TotalAmount <= 0)
        return (null, "Totalt belopp måste vara större än noll.");
    if (invoice.DueDate < invoice.InvoiceDate)
        return (null, "Förfallodatum kan inte vara före fakturadatum.");

    var existing = await _db.SupplierInvoices
        .Include(s => s.FiscalYear)
        .FirstOrDefaultAsync(s => s.Id == invoice.Id).ConfigureAwait(false);

    if (existing is null) return (null, "Fakturan hittades inte.");
    if (existing.JournalEntryId.HasValue) return (null, "Bokförda fakturor kan inte uppdateras.");
    if (existing.IsPaid) return (null, "Betalda fakturor kan inte uppdateras.");
    if (existing.FiscalYear.IsClosed) return (null, "Räkenskapsåret är stängt.");

    existing.SupplierName = invoice.SupplierName;
    existing.InvoiceNumber = invoice.InvoiceNumber;
    existing.InvoiceDate = invoice.InvoiceDate;
    existing.DueDate = invoice.DueDate;
    existing.AmountExclVat = invoice.AmountExclVat;
    existing.VatAmount = invoice.VatAmount;
    existing.TotalAmount = invoice.TotalAmount;
    existing.Notes = invoice.Notes;

    await _db.SaveChangesAsync().ConfigureAwait(false);
    return (existing, null);
}
```

This mirrors `DeleteAsync`'s draft guard (`JournalEntryId.HasValue` / `IsPaid`) exactly, so "draft" means the same thing for update as it does for delete.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SupplierInvoiceReadWriteTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/KoalaBooks.Application/Services/ISupplierInvoiceService.cs src/KoalaBooks.Application/Services/SupplierInvoiceService.cs tests/KoalaBooks.Tests/TestFixture.cs tests/KoalaBooks.Tests/SupplierInvoiceReadWriteTests.cs
git commit -m "feat: add SupplierInvoiceService.GetByIdAsync/UpdateAsync"
```

---

## Task 3: `IBankImportService.GetByFiscalYearAsync` / `GetByIdAsync`

**Files:**
- Modify: `src/KoalaBooks.Domain/Interfaces/IBankImportService.cs`
- Modify: `src/KoalaBooks.Infrastructure/Services/BankImportService.cs`
- Modify: `tests/KoalaBooks.Tests/TestFixture.cs` (expose `BankImportService`)
- Create: `tests/KoalaBooks.Tests/BankTransactionQueryTests.cs`

**Interfaces:**
- Consumes: `BankTransaction` entity (`src/KoalaBooks.Domain/Entities/BankTransaction.cs`) — `Id, AccountId, Account, Date, Amount, Description, Reference, Status, JournalEntryId`. `Account.FiscalYearId` is the join path to a fiscal year (confirmed by `SupplierInvoiceService.FindMatchingBankTransactionsAsync`).
- Produces: `Task<List<BankTransaction>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from, DateOnly? to, int? accountId)`, `Task<BankTransaction?> GetByIdAsync(int id)` — Task 5's controller consumes both by exact name.

- [ ] **Step 1: Expose `BankImportService` on `TestFixture`**

In `tests/KoalaBooks.Tests/TestFixture.cs`, add a property next to `JournalEntryService` (line 19) and construct it next to line 50 (it needs `_currentUser`, already a field on `TestFixture`):

```csharp
public BankImportService BankImportService { get; }
```

```csharp
BankImportService = new BankImportService(Db, _currentUser);
```

- [ ] **Step 2: Write the failing tests**

Create `tests/KoalaBooks.Tests/BankTransactionQueryTests.cs`:

```csharp
using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class BankTransactionQueryTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;

    public BankTransactionQueryTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    private BankTransaction AddTransaction(DateOnly date, decimal amount, string description = "Test tx")
    {
        var tx = new BankTransaction
        {
            OrganisationId = _f.OrganisationId,
            AccountId = _cash.Id,
            Date = date,
            Amount = amount,
            Description = description
        };
        _f.Db.BankTransactions.Add(tx);
        _f.Db.SaveChanges();
        return tx;
    }

    [Fact]
    public async Task GetByFiscalYearAsync_ReturnsAllTransactionsForYear()
    {
        AddTransaction(new DateOnly(2026, 2, 1), 100m);
        AddTransaction(new DateOnly(2026, 3, 1), -50m);

        var result = await _f.BankImportService.GetByFiscalYearAsync(_fy.Id, null, null, null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetByFiscalYearAsync_FiltersByDateRange()
    {
        AddTransaction(new DateOnly(2026, 1, 15), 100m);
        AddTransaction(new DateOnly(2026, 6, 15), 200m);

        var result = await _f.BankImportService.GetByFiscalYearAsync(
            _fy.Id, new DateOnly(2026, 5, 1), new DateOnly(2026, 12, 31), null);

        Assert.Single(result);
        Assert.Equal(200m, result[0].Amount);
    }

    [Fact]
    public async Task GetByFiscalYearAsync_FiltersByAccountId()
    {
        // _cash ("1910") already exists from the constructor's CreateStandardAccounts call —
        // use a distinct account number here, not another CreateStandardAccounts call, which
        // would violate the unique (FiscalYearId, AccountNumber) index.
        var otherAccount = _f.CreateAccount(_fy.Id, "1930", "Sparkonto");
        AddTransaction(new DateOnly(2026, 2, 1), 100m);
        var other = new BankTransaction
        {
            OrganisationId = _f.OrganisationId, AccountId = otherAccount.Id,
            Date = new DateOnly(2026, 2, 2), Amount = 50m, Description = "Other account"
        };
        _f.Db.BankTransactions.Add(other);
        _f.Db.SaveChanges();

        var result = await _f.BankImportService.GetByFiscalYearAsync(_fy.Id, null, null, _cash.Id);

        Assert.Single(result);
        Assert.Equal(_cash.Id, result[0].AccountId);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingTransaction_ReturnsIt()
    {
        var tx = AddTransaction(new DateOnly(2026, 2, 1), 100m, "Findable");

        var found = await _f.BankImportService.GetByIdAsync(tx.Id);

        Assert.NotNull(found);
        Assert.Equal("Findable", found.Description);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var found = await _f.BankImportService.GetByIdAsync(999999);
        Assert.Null(found);
    }
}
```

Note: `_f.CreateStandardAccounts` (called once, in the constructor) can't be called a second time within the same fiscal year — it always creates accounts "1910"/"2440"/"2081"/"3010"/"5010", and `Account` has a unique index on `(FiscalYearId, AccountNumber)`. The account-filter test above uses `_f.CreateAccount(_fy.Id, "1930", ...)` instead to get a second, distinct account.

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BankTransactionQueryTests"`
Expected: FAIL to compile — `IBankImportService`/`BankImportService` have no `GetByFiscalYearAsync`/`GetByIdAsync` yet.

- [ ] **Step 4: Add the interface members**

In `src/KoalaBooks.Domain/Interfaces/IBankImportService.cs`, add after `GetByAccountAsync`:

```csharp
Task<List<BankTransaction>> GetByFiscalYearAsync(int fiscalYearId, DateOnly? from, DateOnly? to, int? accountId);
Task<BankTransaction?> GetByIdAsync(int id);
```

- [ ] **Step 5: Implement in `BankImportService`**

In `src/KoalaBooks.Infrastructure/Services/BankImportService.cs`, add after `GetByAccountAsync` (line 273):

```csharp
public async Task<List<BankTransaction>> GetByFiscalYearAsync(
    int fiscalYearId, DateOnly? from, DateOnly? to, int? accountId)
{
    var query = _db.BankTransactions
        .Include(b => b.Account)
        .Where(b => b.Account.FiscalYearId == fiscalYearId);

    if (from.HasValue)
        query = query.Where(b => b.Date >= from.Value);
    if (to.HasValue)
        query = query.Where(b => b.Date <= to.Value);
    if (accountId.HasValue)
        query = query.Where(b => b.AccountId == accountId.Value);

    return await query
        .OrderByDescending(b => b.Date)
        .ThenByDescending(b => b.Id)
        .ToListAsync().ConfigureAwait(false);
}

public async Task<BankTransaction?> GetByIdAsync(int id)
{
    return await _db.BankTransactions
        .Include(b => b.Account)
        .FirstOrDefaultAsync(b => b.Id == id).ConfigureAwait(false);
}
```

`GetByIdAsync` relies on `BankTransaction`'s own tenant query filter (`OrganisationId`, `AppDbContext.cs:72-73`) — no extra fiscal-year cross-check is needed, unlike `Account` which has no filter of its own.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~BankTransactionQueryTests"`
Expected: PASS (5 tests).

- [ ] **Step 7: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/KoalaBooks.Domain/Interfaces/IBankImportService.cs src/KoalaBooks.Infrastructure/Services/BankImportService.cs tests/KoalaBooks.Tests/TestFixture.cs tests/KoalaBooks.Tests/BankTransactionQueryTests.cs
git commit -m "feat: add BankImportService.GetByFiscalYearAsync/GetByIdAsync"
```

---

## Task 4: Supplier invoice DTOs, `SupplierInvoicesController`, integration tests

**Files:**
- Create: `src/KoalaBooks.Web/Models/Api/SupplierInvoiceResponse.cs`
- Create: `src/KoalaBooks.Web/Models/Api/CreateSupplierInvoiceRequest.cs`
- Create: `src/KoalaBooks.Web/Models/Api/UpdateSupplierInvoiceRequest.cs`
- Create: `src/KoalaBooks.Web/Controllers/Api/SupplierInvoicesController.cs`
- Modify: `tests/KoalaBooks.Tests/Api/ApiTests.cs` (add `── Supplier invoice tests ──` section)

**Interfaces:**
- Consumes: `ISupplierInvoiceService.GetAllAsync/GetByIdAsync/CreateAsync/UpdateAsync/DeleteAsync` (Task 2), `IFiscalYearService.GetByIdAsync` (existing), `PagedResult<T>` (existing).

- [ ] **Step 1: Write the failing integration tests**

Add to `tests/KoalaBooks.Tests/Api/ApiTests.cs`, as a new section after `── Journal entry tests ──` (after line 531, before the closing `}` of the class):

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ApiTests.SupplierInvoices"`
Expected: FAIL — 404s across the board (route `api/v1/fiscal-years/{id}/supplier-invoices` doesn't exist yet).

- [ ] **Step 3: Create the DTOs**

Create `src/KoalaBooks.Web/Models/Api/SupplierInvoiceResponse.cs`:

```csharp
namespace KoalaBooks.Web.Models.Api;

public record SupplierInvoiceResponse(
    int Id,
    int FiscalYearId,
    string SupplierName,
    string? InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    decimal AmountExclVat,
    decimal VatAmount,
    decimal TotalAmount,
    string? Notes,
    bool IsPaid,
    DateOnly? PaidDate,
    int? JournalEntryId,
    int? PaymentJournalEntryId,
    DateTime CreatedAt);
```

Create `src/KoalaBooks.Web/Models/Api/CreateSupplierInvoiceRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class CreateSupplierInvoiceRequest
{
    [Required]
    public string SupplierName { get; init; } = "";

    public string? InvoiceNumber { get; init; }

    [Required]
    public DateOnly InvoiceDate { get; init; }

    [Required]
    public DateOnly DueDate { get; init; }

    public decimal AmountExclVat { get; init; }

    public decimal VatAmount { get; init; }

    [Required]
    public decimal TotalAmount { get; init; }

    public string? Notes { get; init; }
}
```

Create `src/KoalaBooks.Web/Models/Api/UpdateSupplierInvoiceRequest.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class UpdateSupplierInvoiceRequest
{
    [Required]
    public string SupplierName { get; init; } = "";

    public string? InvoiceNumber { get; init; }

    [Required]
    public DateOnly InvoiceDate { get; init; }

    [Required]
    public DateOnly DueDate { get; init; }

    public decimal AmountExclVat { get; init; }

    public decimal VatAmount { get; init; }

    [Required]
    public decimal TotalAmount { get; init; }

    public string? Notes { get; init; }
}
```

- [ ] **Step 4: Create the controller**

Create `src/KoalaBooks.Web/Controllers/Api/SupplierInvoicesController.cs`:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace KoalaBooks.Web.Controllers.Api;

[ApiController]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[Route("api/v1")]
public class SupplierInvoicesController : ControllerBase
{
    private readonly ISupplierInvoiceService _supplierInvoiceService;
    private readonly IFiscalYearService _fiscalYearService;

    public SupplierInvoicesController(ISupplierInvoiceService supplierInvoiceService, IFiscalYearService fiscalYearService)
    {
        _supplierInvoiceService = supplierInvoiceService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/supplier-invoices")]
    [ProducesResponseType<PagedResult<SupplierInvoiceResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _supplierInvoiceService.GetAllAsync(fiscalYearId);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapInvoice).ToList();

        return Ok(new PagedResult<SupplierInvoiceResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }

    [HttpGet("supplier-invoices/{id:int}")]
    [ProducesResponseType<SupplierInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var invoice = await _supplierInvoiceService.GetByIdAsync(id);
        if (invoice is null) return NotFound();
        return Ok(MapInvoice(invoice));
    }

    [HttpPost("fiscal-years/{fiscalYearId:int}/supplier-invoices")]
    [ProducesResponseType<SupplierInvoiceResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(int fiscalYearId, [FromBody] CreateSupplierInvoiceRequest request)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        var invoice = new SupplierInvoice
        {
            FiscalYearId = fiscalYearId,
            SupplierName = request.SupplierName,
            InvoiceNumber = request.InvoiceNumber,
            InvoiceDate = request.InvoiceDate,
            DueDate = request.DueDate,
            AmountExclVat = request.AmountExclVat,
            VatAmount = request.VatAmount,
            TotalAmount = request.TotalAmount,
            Notes = request.Notes
        };

        var (created, error) = await _supplierInvoiceService.CreateAsync(invoice);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return CreatedAtAction(nameof(GetById), new { id = created!.Id }, MapInvoice(created));
    }

    [HttpPut("supplier-invoices/{id:int}")]
    [ProducesResponseType<SupplierInvoiceResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateSupplierInvoiceRequest request)
    {
        var existing = await _supplierInvoiceService.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var invoice = new SupplierInvoice
        {
            Id = id,
            SupplierName = request.SupplierName,
            InvoiceNumber = request.InvoiceNumber,
            InvoiceDate = request.InvoiceDate,
            DueDate = request.DueDate,
            AmountExclVat = request.AmountExclVat,
            VatAmount = request.VatAmount,
            TotalAmount = request.TotalAmount,
            Notes = request.Notes
        };

        var (updated, error) = await _supplierInvoiceService.UpdateAsync(invoice);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return Ok(MapInvoice(updated!));
    }

    [HttpDelete("supplier-invoices/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var invoice = await _supplierInvoiceService.GetByIdAsync(id);
        if (invoice is null) return NotFound();

        var error = await _supplierInvoiceService.DeleteAsync(id);
        if (error is not null)
            return Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);

        return NoContent();
    }

    private static SupplierInvoiceResponse MapInvoice(SupplierInvoice s) =>
        new(s.Id, s.FiscalYearId, s.SupplierName, s.InvoiceNumber, s.InvoiceDate, s.DueDate,
            s.AmountExclVat, s.VatAmount, s.TotalAmount, s.Notes, s.IsPaid, s.PaidDate,
            s.JournalEntryId, s.PaymentJournalEntryId, s.CreatedAt);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ApiTests.SupplierInvoices"`
Expected: PASS (11 tests).

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add src/KoalaBooks.Web/Models/Api/SupplierInvoiceResponse.cs src/KoalaBooks.Web/Models/Api/CreateSupplierInvoiceRequest.cs src/KoalaBooks.Web/Models/Api/UpdateSupplierInvoiceRequest.cs src/KoalaBooks.Web/Controllers/Api/SupplierInvoicesController.cs tests/KoalaBooks.Tests/Api/ApiTests.cs
git commit -m "feat: add supplier invoices REST API endpoints (#121)"
```

---

## Task 5: Bank transaction DTO, `BankTransactionsController`, integration tests

**Files:**
- Create: `src/KoalaBooks.Web/Models/Api/BankTransactionResponse.cs`
- Create: `src/KoalaBooks.Web/Controllers/Api/BankTransactionsController.cs`
- Modify: `tests/KoalaBooks.Tests/Api/ApiTests.cs` (add `── Bank transaction tests ──` section)

**Interfaces:**
- Consumes: `IBankImportService.GetByFiscalYearAsync/GetByIdAsync` (Task 3), `IFiscalYearService.GetByIdAsync` (existing), `PagedResult<T>` (existing).

- [ ] **Step 1: Write the failing integration tests**

Add to `tests/KoalaBooks.Tests/Api/ApiTests.cs`, as a new section after `── Supplier invoice tests ──` (before the closing `}` of the class):

```csharp
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
```

Note: `Accounts.IgnoreQueryFilters()` is required inside the manually-created `IServiceScope` blocks because, like the existing `JournalEntries_Reverse_PostedEntry_ReturnsReversalLinkedToOriginal` test explains (`ApiTests.cs:472-477`), a scope created outside an HTTP request has no ambient `ICurrentUser.OrganisationId`, so the tenant query filter would otherwise return nothing.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ApiTests.BankTransactions"`
Expected: FAIL — 404s (route doesn't exist yet).

- [ ] **Step 3: Create the DTO**

Create `src/KoalaBooks.Web/Models/Api/BankTransactionResponse.cs`:

```csharp
using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record BankTransactionResponse(
    int Id,
    int AccountId,
    string AccountNumber,
    DateOnly Date,
    decimal Amount,
    string Description,
    string? Reference,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BankTransactionStatus Status,
    int? JournalEntryId);
```

- [ ] **Step 4: Create the controller**

Create `src/KoalaBooks.Web/Controllers/Api/BankTransactionsController.cs`:

```csharp
using KoalaBooks.Application.Services;
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
public class BankTransactionsController : ControllerBase
{
    private readonly IBankImportService _bankImportService;
    private readonly IFiscalYearService _fiscalYearService;

    public BankTransactionsController(IBankImportService bankImportService, IFiscalYearService fiscalYearService)
    {
        _bankImportService = bankImportService;
        _fiscalYearService = fiscalYearService;
    }

    [HttpGet("fiscal-years/{fiscalYearId:int}/bank-transactions")]
    [ProducesResponseType<PagedResult<BankTransactionResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByFiscalYear(
        int fiscalYearId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? accountId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var fy = await _fiscalYearService.GetByIdAsync(fiscalYearId);
        if (fy is null) return NotFound();

        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(1, page);

        var all = await _bankImportService.GetByFiscalYearAsync(fiscalYearId, from, to, accountId);
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(MapTransaction).ToList();

        return Ok(new PagedResult<BankTransactionResponse>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        });
    }

    [HttpGet("bank-transactions/{id:int}")]
    [ProducesResponseType<BankTransactionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        var tx = await _bankImportService.GetByIdAsync(id);
        if (tx is null) return NotFound();
        return Ok(MapTransaction(tx));
    }

    private static BankTransactionResponse MapTransaction(BankTransaction b) =>
        new(b.Id, b.AccountId, b.Account?.AccountNumber ?? "", b.Date, b.Amount, b.Description,
            b.Reference, b.Status, b.JournalEntryId);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ApiTests.BankTransactions"`
Expected: PASS (6 tests).

- [ ] **Step 6: Run the full suite and commit**

Run: `dotnet test`
Expected: PASS — every test in the solution (existing + all tests added by this plan) passes.

```bash
git add src/KoalaBooks.Web/Models/Api/BankTransactionResponse.cs src/KoalaBooks.Web/Controllers/Api/BankTransactionsController.cs tests/KoalaBooks.Tests/Api/ApiTests.cs
git commit -m "feat: add bank transactions read-only REST API endpoints (#121)"
```

---

## Note on issue #257 (not part of this plan)

Issue #257 ("Auth work needed for a browser-hosted WASM/Auto client") is unrelated in scope — it's about bridging server-side Blazor auth into WASM-rendered components, either via `PersistentAuthenticationStateProvider` or the authorization-code+PKCE flow that's already configured in OpenIddict. It doesn't block or get blocked by #120/#121, and none of the endpoints or client-seeding logic in this plan need to change to accommodate it: any token this API validates — however it was obtained — flows through the same `AddValidation().UseLocalServer()` pipeline already exercised by every test here.

The one deliberate accommodation already made: Task 1 seeds `ApiClientSeeder.SeedAsync` as a single unconditional call after the `Testing`/non-`Testing` split in `Program.cs`, specifically so a future WASM or MAUI client's own OpenIddict client registration (#257, #63) can be added as one more call at that same site without re-touching the `if/else` branching logic again.
