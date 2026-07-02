# Demo Data Seed (Dev + PR Previews) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bare-bones dev-only seed (org + user, no books data) with a richer `DemoDataSeeder` that also seeds a full BAS chart of accounts, an open fiscal year, and posted journal entries with one deliberate voucher-number gap — running automatically in local `Development` and via an explicit `SEED_DEMO_DATA=true` flag in PR previews.

**Architecture:** A new static `DemoDataSeeder.SeedAsync(IServiceProvider)` class in `KoalaBooks.Infrastructure.Services`, following the exact pattern of the existing `AspireDashboardSeeder`. It builds its own tenant-scoped `AppDbContext` (via `LocalCurrentUser`) instead of the DI-resolved one, because the DI-resolved `AppDbContext` relies on `HttpContextCurrentUser`, which returns `null` for `OrganisationId` outside an HTTP request — and every tenant-scoped table (`FiscalYears`, `Accounts`, `JournalEntries`, `JournalEntryLines`) has a global EF Core query filter keyed on `ICurrentUser.OrganisationId`. Without this, every query the seeder makes after creating the org would silently return empty results.

**Tech Stack:** .NET / EF Core / ASP.NET Core Identity, xUnit + Testcontainers (existing `PostgresContainerFixture`), Docker Compose.

## Global Constraints

- Reuse existing seed credentials: `admin@koalabooks.local` / `Admin123!` (already satisfies the app's configured password policy: digit, upper, lower, length ≥ 8).
- Reuse the existing `BasImportService.ImportDefaultAsync(fiscalYearId)` for the chart of accounts — do not hand-maintain a separate account list.
- Reuse the existing `JournalEntryService.CreateAsync` / `PostAsync` validated path for posting entries — do not write entries directly via `SaveChangesAsync` (except for the one deliberate gap-creating deletion, which must bypass the service on purpose).
- Seeding must be idempotent: a second call to `SeedAsync` with the demo user already present must be a no-op.
- Seeding must never run when `ASPNETCORE_ENVIRONMENT=Production` and must never run in the `Testing` environment (already guaranteed by the existing `if (app.Environment.IsEnvironment("Testing")) { ... } else { ... }` structure in `Program.cs`, which this plan does not change).

---

### Task 1: `DemoDataSeeder` — organisation + login-capable user

**Files:**
- Create: `src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs`
- Test: `tests/KoalaBooks.Tests/DemoDataSeederTests.cs`

**Interfaces:**
- Produces: `public static class DemoDataSeeder` with `public const string DemoUserEmail = "admin@koalabooks.local";`, `public const string DemoUserPassword = "Admin123!";`, and `public static Task SeedAsync(IServiceProvider services)`. Later tasks extend the body of `SeedAsync` in place — the signature does not change.

- [ ] **Step 1: Write the failing tests**

Create `tests/KoalaBooks.Tests/DemoDataSeederTests.cs`:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Tests;

public class DemoDataSeederTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly string _dbName;

    public DemoDataSeederTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser>(new LocalCurrentUser());
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connStr));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<AppDbContext>();

        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// The DI-registered AppDbContext is tied to a singleton LocalCurrentUser with
    /// OrganisationId = null, so it can't see tenant-scoped rows. Organisations has no
    /// tenant filter, so we read the seeded org id from it, then open a second
    /// AppDbContext scoped to that org for verifying tenant-scoped data.
    /// </summary>
    private async Task<(AppDbContext Db, int OrganisationId)> OpenTenantDbAsync(IServiceProvider services)
    {
        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        await using var untenanted = new AppDbContext(options, new LocalCurrentUser());
        var orgId = await untenanted.Organisations.Select(o => o.Id).SingleAsync();
        return (new AppDbContext(options, new LocalCurrentUser(orgId)), orgId);
    }

    [Fact]
    public async Task SeedAsync_CreatesLoginableDemoUser()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(DemoDataSeeder.DemoUserEmail);

        Assert.NotNull(user);
        Assert.True(await userManager.CheckPasswordAsync(user!, DemoDataSeeder.DemoUserPassword));
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent()
    {
        using (var scope = _sp.CreateScope())
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        using (var scope = _sp.CreateScope())
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        using var verifyScope = _sp.CreateScope();
        var (db, _) = await OpenTenantDbAsync(verifyScope.ServiceProvider);
        await using (db)
        {
            var options = verifyScope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
            await using var untenanted = new AppDbContext(options, new LocalCurrentUser());
            Assert.Equal(1, await untenanted.Organisations.CountAsync());
        }
    }

    public void Dispose()
    {
        _sp.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: FAIL to compile — `DemoDataSeeder` does not exist.

- [ ] **Step 3: Implement `DemoDataSeeder` (org + user only)**

Create `src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs`:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Infrastructure.Services;

public static class DemoDataSeeder
{
    public const string DemoUserEmail = "admin@koalabooks.local";
    public const string DemoUserPassword = "Admin123!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(DemoUserEmail) is not null)
            return;

        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        var tenant = new LocalCurrentUser();
        await using var db = new AppDbContext(options, tenant);

        var org = await db.Organisations.FirstOrDefaultAsync(o => o.Slug == "demo");
        if (org is null)
        {
            org = new Organisation { Name = "Demo AB", Slug = "demo", LegalForm = LegalForm.Aktiebolag };
            db.Organisations.Add(org);
            await db.SaveChangesAsync();
        }
        tenant.OrganisationId = org.Id;

        var demoUser = new ApplicationUser
        {
            UserName = DemoUserEmail,
            Email = DemoUserEmail,
            EmailConfirmed = true,
            DisplayName = "Admin",
            OrganisationId = org.Id
        };
        var createResult = await userManager.CreateAsync(demoUser, DemoUserPassword);
        if (!createResult.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create demo user: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs tests/KoalaBooks.Tests/DemoDataSeederTests.cs
git commit -m "feat: add DemoDataSeeder with org and login-capable demo user"
```

---

### Task 2: Fiscal year + full BAS chart of accounts

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs`
- Modify: `tests/KoalaBooks.Tests/DemoDataSeederTests.cs`

**Interfaces:**
- Consumes: `BasImportService.ImportDefaultAsync(int fiscalYearId)` from `src/KoalaBooks.Infrastructure/Services/BasImportService.cs` (same namespace, no new `using` needed).
- Produces: `SeedAsync` now also creates a `FiscalYear` for the current calendar year and imports the full BAS 2026 chart of accounts into it.

- [ ] **Step 1: Write the failing test**

Add to `DemoDataSeederTests.cs` (inside the class, after `SeedAsync_IsIdempotent`):

```csharp
    [Fact]
    public async Task SeedAsync_ImportsBasChartOfAccounts()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var accountNumbers = await db.Accounts.Select(a => a.AccountNumber).ToListAsync();
            Assert.True(accountNumbers.Count > 1000, $"Expected a full BAS import, got {accountNumbers.Count} accounts.");
            foreach (var expected in new[] { "1910", "2440", "2081", "3010", "5010" })
                Assert.Contains(expected, accountNumbers);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SeedAsync_ImportsBasChartOfAccounts`
Expected: FAIL — `accountNumbers.Count` is 0.

- [ ] **Step 3: Extend `DemoDataSeeder.SeedAsync`**

Replace the entire contents of `src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs` with:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Infrastructure.Services;

public static class DemoDataSeeder
{
    public const string DemoUserEmail = "admin@koalabooks.local";
    public const string DemoUserPassword = "Admin123!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(DemoUserEmail) is not null)
            return;

        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        var tenant = new LocalCurrentUser();
        await using var db = new AppDbContext(options, tenant);

        var org = await db.Organisations.FirstOrDefaultAsync(o => o.Slug == "demo");
        if (org is null)
        {
            org = new Organisation { Name = "Demo AB", Slug = "demo", LegalForm = LegalForm.Aktiebolag };
            db.Organisations.Add(org);
            await db.SaveChangesAsync();
        }
        tenant.OrganisationId = org.Id;

        var demoUser = new ApplicationUser
        {
            UserName = DemoUserEmail,
            Email = DemoUserEmail,
            EmailConfirmed = true,
            DisplayName = "Admin",
            OrganisationId = org.Id
        };
        var createResult = await userManager.CreateAsync(demoUser, DemoUserPassword);
        if (!createResult.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create demo user: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");

        var year = DateTime.UtcNow.Year;
        var fiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = year.ToString(),
            StartDate = new DateOnly(year, 1, 1),
            EndDate = new DateOnly(year, 12, 31),
            IsClosed = false
        };
        db.FiscalYears.Add(fiscalYear);
        await db.SaveChangesAsync();

        await new BasImportService(db).ImportDefaultAsync(fiscalYear.Id);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs tests/KoalaBooks.Tests/DemoDataSeederTests.cs
git commit -m "feat: seed fiscal year and full BAS chart of accounts in DemoDataSeeder"
```

---

### Task 3: Posted journal entries with a deliberate voucher-number gap

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs`
- Modify: `tests/KoalaBooks.Tests/DemoDataSeederTests.cs`

**Interfaces:**
- Consumes: `JournalEntryService.CreateAsync(JournalEntry)` returning `(JournalEntry? Entry, string? Error)`, and `JournalEntryService.PostAsync(int entryId)` returning `string?` (error or null) — both from `KoalaBooks.Application.Services`.
- Produces: after `SeedAsync` completes, the seeded fiscal year has 5 posted journal entries (numbers 1, 2, 4, 5, 6) with entry number 3 missing.
- Note: this task has no dependency on the separate `VoucherGapService`/voucher-gap-detection feature (a different, unmerged PR) — the gap is verified purely as a fact about `JournalEntry.EntryNumber` values, so this builds and passes standalone against `main`.

- [ ] **Step 1: Write the failing test**

Add to `DemoDataSeederTests.cs`:

```csharp
    [Fact]
    public async Task SeedAsync_LeavesOneVoucherGap()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var entryNumbers = await db.JournalEntries
                .OrderBy(j => j.EntryNumber)
                .Select(j => j.EntryNumber)
                .ToListAsync();
            Assert.Equal([1, 2, 4, 5, 6], entryNumbers);
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SeedAsync_LeavesOneVoucherGap`
Expected: FAIL — `gaps` is empty (no journal entries exist yet).

- [ ] **Step 3: Extend `DemoDataSeeder`**

Replace the entire contents of `src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs` with:

```csharp
using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Infrastructure.Services;

public static class DemoDataSeeder
{
    public const string DemoUserEmail = "admin@koalabooks.local";
    public const string DemoUserPassword = "Admin123!";

    public static async Task SeedAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(DemoUserEmail) is not null)
            return;

        var options = services.GetRequiredService<DbContextOptions<AppDbContext>>();
        var tenant = new LocalCurrentUser();
        await using var db = new AppDbContext(options, tenant);

        var org = await db.Organisations.FirstOrDefaultAsync(o => o.Slug == "demo");
        if (org is null)
        {
            org = new Organisation { Name = "Demo AB", Slug = "demo", LegalForm = LegalForm.Aktiebolag };
            db.Organisations.Add(org);
            await db.SaveChangesAsync();
        }
        tenant.OrganisationId = org.Id;

        var demoUser = new ApplicationUser
        {
            UserName = DemoUserEmail,
            Email = DemoUserEmail,
            EmailConfirmed = true,
            DisplayName = "Admin",
            OrganisationId = org.Id
        };
        var createResult = await userManager.CreateAsync(demoUser, DemoUserPassword);
        if (!createResult.Succeeded)
            throw new InvalidOperationException(
                $"Failed to create demo user: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");

        var year = DateTime.UtcNow.Year;
        var fiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = year.ToString(),
            StartDate = new DateOnly(year, 1, 1),
            EndDate = new DateOnly(year, 12, 31),
            IsClosed = false
        };
        db.FiscalYears.Add(fiscalYear);
        await db.SaveChangesAsync();

        await new BasImportService(db).ImportDefaultAsync(fiscalYear.Id);

        await SeedJournalEntriesAsync(db, fiscalYear.Id);
    }

    private static async Task SeedJournalEntriesAsync(AppDbContext db, int fiscalYearId)
    {
        var accounts = await db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Where(a => a.AccountNumber == "1910" || a.AccountNumber == "2440"
                || a.AccountNumber == "2081" || a.AccountNumber == "3010" || a.AccountNumber == "5010")
            .ToDictionaryAsync(a => a.AccountNumber);

        var cash = accounts["1910"].Id;
        var payables = accounts["2440"].Id;
        var equity = accounts["2081"].Id;
        var revenue = accounts["3010"].Id;
        var expense = accounts["5010"].Id;

        (int DebitAccountId, int CreditAccountId, decimal Amount, string Description)[] entries =
        [
            (cash, equity, 50000m, "Aktiekapital"),
            (cash, revenue, 12000m, "Kontantförsäljning"),
            (expense, cash, 8000m, "Lokalhyra"),
            (cash, revenue, 15000m, "Kontantförsäljning"),
            (payables, cash, 3000m, "Betalning leverantörsfaktura"),
            (expense, payables, 4500m, "Inköp material")
        ];

        var journalEntryService = new JournalEntryService(db);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var postedIds = new List<int>();

        foreach (var (debitAccountId, creditAccountId, amount, description) in entries)
        {
            var entry = new JournalEntry
            {
                Date = today,
                Description = description,
                FiscalYearId = fiscalYearId,
                Lines =
                [
                    new() { AccountId = debitAccountId, DebitAmount = amount, CreditAmount = 0 },
                    new() { AccountId = creditAccountId, DebitAmount = 0, CreditAmount = amount }
                ]
            };

            var (created, error) = await journalEntryService.CreateAsync(entry);
            if (error is not null)
                throw new InvalidOperationException($"Demo seed failed to create journal entry '{description}': {error}");

            await journalEntryService.PostAsync(created!.Id);
            postedIds.Add(created.Id);
        }

        // Leave voucher #3 as a gap: bypass JournalEntryService (which blocks deleting posted
        // entries) to simulate a historical direct-DB deletion — the exact scenario BFNAR 2013:2
        // gap detection exists to catch.
        var gapEntry = await db.JournalEntries
            .Include(j => j.Lines)
            .FirstAsync(j => j.Id == postedIds[2]);
        db.JournalEntryLines.RemoveRange(gapEntry.Lines);
        db.JournalEntries.Remove(gapEntry);
        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/DemoDataSeeder.cs tests/KoalaBooks.Tests/DemoDataSeederTests.cs
git commit -m "feat: seed posted journal entries with a deliberate voucher gap"
```

---

### Task 4: Wire `DemoDataSeeder` into `Program.cs`

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs:3-4` (usings), `src/KoalaBooks.Web/Program.cs:216-237` (seed block)

**Interfaces:**
- Consumes: `DemoDataSeeder.SeedAsync(IServiceProvider)` from Task 1-3.

- [ ] **Step 1: Remove now-unused usings**

In `src/KoalaBooks.Web/Program.cs`, delete these two lines (they become unused once the inline `Organisation`/`LegalForm` seed code below is removed):

```csharp
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
```

(Leave `using KoalaBooks.Domain.Interfaces;` — `ICurrentUser` is still used elsewhere in this file.)

- [ ] **Step 2: Replace the inline seed block**

In `src/KoalaBooks.Web/Program.cs`, replace:

```csharp
        if (app.Environment.IsDevelopment())
        {
            // Seed a default org + dev user if none exists
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            const string devEmail = "admin@koalabooks.local";
            if (await userManager.FindByEmailAsync(devEmail) is null)
            {
                var org = new Organisation { Name = "Dev Organisation", Slug = "dev", LegalForm = LegalForm.Aktiebolag };
                db.Organisations.Add(org);
                await db.SaveChangesAsync();

                var devUser = new ApplicationUser
                {
                    UserName = devEmail,
                    Email = devEmail,
                    EmailConfirmed = true,
                    DisplayName = "Admin",
                    OrganisationId = org.Id
                };
                await userManager.CreateAsync(devUser, "Admin123!");
            }
        }
```

with:

```csharp
        if (app.Environment.IsDevelopment() || builder.Configuration["SEED_DEMO_DATA"] == "true")
        {
            await DemoDataSeeder.SeedAsync(scope.ServiceProvider);
        }
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build`
Expected: Build succeeded, 0 errors. (If `Organisation`/`LegalForm` usings were still needed elsewhere, this step catches it — undo the using removal from Step 1 for whichever one is flagged.)

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: PASS — all existing tests (including `Api`/`Oidc`/`TenantIsolation` tests, which run in `Testing` environment and never reach this seed block) continue to pass, plus the 4 new `DemoDataSeederTests`.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Web/Program.cs
git commit -m "feat: wire DemoDataSeeder into app startup for dev and preview environments"
```

---

### Task 5: Enable demo seeding in PR previews

**Files:**
- Modify: `docker-compose.pr-preview.yml`

**Interfaces:**
- Consumes: the `SEED_DEMO_DATA` configuration key read in `Program.cs` (Task 4).

- [ ] **Step 1: Add the env var to the preview compose template**

In `docker-compose.pr-preview.yml`, change:

```yaml
services:
  web:
    image: ghcr.io/__OWNER__/koalabooks-web:pr-__PR_NUMBER__
    environment:
      - ConnectionStrings__koalabooks=Host=postgres;Port=5432;Database=koalabooks;Username=koalabooks;Password=__POSTGRES_PASSWORD__
      - ASPNETCORE_ENVIRONMENT=Staging
      - ASPNETCORE_URLS=http://+:8080
```

to:

```yaml
services:
  web:
    image: ghcr.io/__OWNER__/koalabooks-web:pr-__PR_NUMBER__
    environment:
      - ConnectionStrings__koalabooks=Host=postgres;Port=5432;Database=koalabooks;Username=koalabooks;Password=__POSTGRES_PASSWORD__
      - ASPNETCORE_ENVIRONMENT=Staging
      - ASPNETCORE_URLS=http://+:8080
      - SEED_DEMO_DATA=true
```

- [ ] **Step 2: Validate the compose file syntax**

Run: `docker compose -f docker-compose.pr-preview.yml config --quiet`
Expected: no output, exit code 0 (confirms valid YAML/compose syntax; the `__PR_NUMBER__`/`__OWNER__`/`__POSTGRES_PASSWORD__` placeholders are template tokens substituted by the deploy workflow at deploy time, not by `docker compose config`, so this only checks structure).

- [ ] **Step 3: Commit**

```bash
git add docker-compose.pr-preview.yml
git commit -m "feat: enable demo data seeding in PR preview environments"
```

---

## Manual Verification (after all tasks)

This can't be fully covered by unit tests since it depends on a real deployed preview. After the next PR preview deploys with these changes:

1. Open `https://pr-<n>.books.koalasoft.se`.
2. Log in with `admin@koalabooks.local` / `Admin123!`.
3. Confirm the "Demo AB" organisation is active, the current fiscal year exists with a full BAS chart of accounts, and the journal shows 5 posted entries with voucher number 3 visibly skipped.
4. If the separate voucher-gap-detection feature (PR #178) has since merged, also confirm entry #3 shows up there as an unexplained gap — this seed doesn't depend on that feature, but is designed to exercise it.
