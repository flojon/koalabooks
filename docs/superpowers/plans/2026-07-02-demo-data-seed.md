# Demo Data Seed (Dev + PR Previews) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bare-bones dev-only seed (org + user, no books data) with a richer `DemoDataSeeder` that also seeds a full BAS chart of accounts, two fiscal years (a closed prior year and an open current year) with posted journal entries spread across different months, and one deliberate voucher-number gap in the current year — running automatically in local `Development` and via an explicit `SEED_DEMO_DATA=true` flag in PR previews.

**Architecture:** A new static `DemoDataSeeder.SeedAsync(IServiceProvider)` class in `KoalaBooks.Application.Services`, following the exact pattern of the existing `AspireDashboardSeeder` (which lives in `KoalaBooks.Infrastructure.Services` — `DemoDataSeeder` cannot live there too, because `KoalaBooks.Application` already project-references `KoalaBooks.Infrastructure`, and the seeder needs both `BasImportService` (Infrastructure) and `JournalEntryService` (Application); placing it in Infrastructure would require Infrastructure to reference Application back, a circular project reference). It builds its own tenant-scoped `AppDbContext` (via `LocalCurrentUser`) instead of the DI-resolved one, because the DI-resolved `AppDbContext` relies on `HttpContextCurrentUser`, which returns `null` for `OrganisationId` outside an HTTP request — and every tenant-scoped table (`FiscalYears`, `Accounts`, `JournalEntries`, `JournalEntryLines`) has a global EF Core query filter keyed on `ICurrentUser.OrganisationId`. Without this, every query the seeder makes after creating the org would silently return empty results.

**Tech Stack:** .NET / EF Core / ASP.NET Core Identity, xUnit + Testcontainers (existing `PostgresContainerFixture`), Docker Compose.

## Global Constraints

- Reuse existing seed credentials: `admin@koalabooks.local` / `Admin123!` (already satisfies the app's configured password policy: digit, upper, lower, length ≥ 8).
- Reuse the existing `BasImportService.ImportDefaultAsync(fiscalYearId)` for the chart of accounts — do not hand-maintain a separate account list. Accounts are per-fiscal-year, so this is called once per fiscal year.
- Reuse the existing `JournalEntryService.CreateAsync` / `PostAsync` validated path for posting entries — do not write entries directly via `SaveChangesAsync` (except for the one deliberate gap-creating deletion, which must bypass the service on purpose).
- Account `3010` does **not** exist in the embedded BAS 2026 kontoplan (verified directly against the imported data — only 3000–3004 exist in the 30xx range). Use `3001` ("Försäljning inom Sverige, 25 % moms") as the revenue account everywhere instead.
- Seeding must be idempotent: a second call to `SeedAsync` with the demo user already present must be a no-op.
- Seeding must never run when `ASPNETCORE_ENVIRONMENT=Production` and must never run in the `Testing` environment (already guaranteed by the existing `if (app.Environment.IsEnvironment("Testing")) { ... } else { ... }` structure in `Program.cs`, which this plan does not change).

---

### Task 1: `DemoDataSeeder` — organisation + login-capable user

**Status:** Already implemented and merged into this branch (commits through `e917497`). Included here only for reference — do not redo.

**Files:**
- Create: `src/KoalaBooks.Application/Services/DemoDataSeeder.cs`
- Test: `tests/KoalaBooks.Tests/DemoDataSeederTests.cs`

**Interfaces:**
- Produces: `public static class DemoDataSeeder` with `public const string DemoUserEmail = "admin@koalabooks.local";`, `public const string DemoUserPassword = "Admin123!";`, and `public static Task SeedAsync(IServiceProvider services)`. Later tasks extend the body of `SeedAsync` in place — the signature does not change.

Final state of `src/KoalaBooks.Application/Services/DemoDataSeeder.cs` after this task (org lookup-by-slug and `IdentityResult` check were added during task review — this is the as-built version):

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Application.Services;

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

`tests/KoalaBooks.Tests/DemoDataSeederTests.cs` as of this task has the test-fixture scaffolding (`_sp`, `_dbName`, constructor, `OpenTenantDbAsync` helper, `Dispose`) plus `SeedAsync_CreatesLoginableDemoUser` and `SeedAsync_IsIdempotent`. Later tasks add more `[Fact]` methods to this same file — do not recreate the scaffolding.

---

### Task 2: Two fiscal years + full BAS chart of accounts in both

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DemoDataSeeder.cs`
- Modify: `tests/KoalaBooks.Tests/DemoDataSeederTests.cs`

**Interfaces:**
- Consumes: `BasImportService.ImportDefaultAsync(int fiscalYearId)` from `src/KoalaBooks.Infrastructure/Services/BasImportService.cs` — `DemoDataSeeder` lives in `KoalaBooks.Application.Services`, a different namespace, so this requires `using KoalaBooks.Infrastructure.Services;`.
- Produces: `SeedAsync` now also creates two `FiscalYear` rows — previous calendar year and current calendar year, both still `IsClosed = false` at the end of this task (Task 3 closes the previous year after posting its entries) — and imports the full BAS 2026 chart of accounts into each.

- [ ] **Step 1: Write the failing tests**

Add to `DemoDataSeederTests.cs` (inside the class, after `SeedAsync_IsIdempotent`):

```csharp
    [Fact]
    public async Task SeedAsync_CreatesTwoFiscalYears()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var years = await db.FiscalYears.OrderBy(f => f.Name).ToListAsync();
            Assert.Equal(2, years.Count);

            var currentYear = DateTime.UtcNow.Year;
            Assert.Equal((currentYear - 1).ToString(), years[0].Name);
            Assert.Equal(currentYear.ToString(), years[1].Name);
        }
    }

    [Fact]
    public async Task SeedAsync_ImportsBasChartOfAccounts()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var fiscalYearIds = await db.FiscalYears.Select(f => f.Id).ToListAsync();
            Assert.Equal(2, fiscalYearIds.Count);

            foreach (var fiscalYearId in fiscalYearIds)
            {
                var accountNumbers = await db.Accounts
                    .Where(a => a.FiscalYearId == fiscalYearId)
                    .Select(a => a.AccountNumber)
                    .ToListAsync();
                Assert.True(accountNumbers.Count > 1000,
                    $"Expected a full BAS import for fiscal year {fiscalYearId}, got {accountNumbers.Count} accounts.");
                foreach (var expected in new[] { "1910", "2440", "2081", "3001", "5010" })
                    Assert.Contains(expected, accountNumbers);
            }
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: FAIL — the 2 new tests fail (no fiscal years exist yet); the 2 tests from Task 1 still pass.

- [ ] **Step 3: Extend `DemoDataSeeder.SeedAsync`**

Replace the entire contents of `src/KoalaBooks.Application/Services/DemoDataSeeder.cs` with:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Application.Services;

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

        var currentYearNumber = DateTime.UtcNow.Year;

        var previousFiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = (currentYearNumber - 1).ToString(),
            StartDate = new DateOnly(currentYearNumber - 1, 1, 1),
            EndDate = new DateOnly(currentYearNumber - 1, 12, 31),
            IsClosed = false
        };
        var currentFiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = currentYearNumber.ToString(),
            StartDate = new DateOnly(currentYearNumber, 1, 1),
            EndDate = new DateOnly(currentYearNumber, 12, 31),
            IsClosed = false
        };
        db.FiscalYears.AddRange(previousFiscalYear, currentFiscalYear);
        await db.SaveChangesAsync();

        await new BasImportService(db).ImportDefaultAsync(previousFiscalYear.Id);
        await new BasImportService(db).ImportDefaultAsync(currentFiscalYear.Id);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/DemoDataSeeder.cs tests/KoalaBooks.Tests/DemoDataSeederTests.cs
git commit -m "feat: seed two fiscal years and full BAS chart of accounts in DemoDataSeeder"
```

---

### Task 3: Posted journal entries across both years, spread by month, with a voucher gap in the current year

**Files:**
- Modify: `src/KoalaBooks.Application/Services/DemoDataSeeder.cs`
- Modify: `tests/KoalaBooks.Tests/DemoDataSeederTests.cs`

**Interfaces:**
- Consumes: `JournalEntryService.CreateAsync(JournalEntry)` returning `(JournalEntry? Entry, string? Error)`, and `JournalEntryService.PostAsync(int entryId)` returning `string?` (error or null) — both from `KoalaBooks.Application.Services`, the same namespace `DemoDataSeeder` now lives in (see Task 1's Architecture note), so no new `using` is needed for `JournalEntryService` itself.
- Produces: the previous fiscal year ends up with 4 posted entries spread across 4 different months, no gap, and `IsClosed = true` / `ClosedAt` set. The current fiscal year ends up with 6 posted entries spread one-per-month January–June, with entry number 3 deleted afterward (numbers present: 1, 2, 4, 5, 6).
- Note: this task has no dependency on the separate `VoucherGapService`/voucher-gap-detection feature (a different, unmerged PR) — the gap is verified purely as a fact about `JournalEntry.EntryNumber` values, so this builds and passes standalone against `main`.

- [ ] **Step 1: Write the failing tests**

Add to `DemoDataSeederTests.cs`:

```csharp
    [Fact]
    public async Task SeedAsync_LeavesOneVoucherGapInCurrentYear()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var currentFiscalYearId = await db.FiscalYears
                .Where(f => !f.IsClosed)
                .Select(f => f.Id)
                .SingleAsync();

            var entryNumbers = await db.JournalEntries
                .Where(j => j.FiscalYearId == currentFiscalYearId)
                .OrderBy(j => j.EntryNumber)
                .Select(j => j.EntryNumber)
                .ToListAsync();
            Assert.Equal([1, 2, 4, 5, 6], entryNumbers);
        }
    }

    [Fact]
    public async Task SeedAsync_SpreadsCurrentYearEntriesAcrossMonths()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var currentFiscalYearId = await db.FiscalYears
                .Where(f => !f.IsClosed)
                .Select(f => f.Id)
                .SingleAsync();

            var months = await db.JournalEntries
                .Where(j => j.FiscalYearId == currentFiscalYearId)
                .Select(j => j.Date.Month)
                .Distinct()
                .ToListAsync();
            Assert.True(months.Count >= 5, $"Expected entries spread across at least 5 distinct months, got {months.Count}.");
        }
    }

    [Fact]
    public async Task SeedAsync_ClosesPreviousYearWithFourEntries()
    {
        using var scope = _sp.CreateScope();
        await DemoDataSeeder.SeedAsync(scope.ServiceProvider);

        var (db, _) = await OpenTenantDbAsync(scope.ServiceProvider);
        await using (db)
        {
            var previousFiscalYear = await db.FiscalYears
                .Where(f => f.IsClosed)
                .SingleAsync();
            Assert.NotNull(previousFiscalYear.ClosedAt);

            var entryCount = await db.JournalEntries.CountAsync(j => j.FiscalYearId == previousFiscalYear.Id);
            Assert.Equal(4, entryCount);
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: FAIL — the 3 new tests fail (no journal entries exist yet, no fiscal year is closed yet); the 4 tests from Tasks 1-2 still pass.

- [ ] **Step 3: Extend `DemoDataSeeder`**

Replace the entire contents of `src/KoalaBooks.Application/Services/DemoDataSeeder.cs` with:

```csharp
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KoalaBooks.Application.Services;

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

        var currentYearNumber = DateTime.UtcNow.Year;

        var previousFiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = (currentYearNumber - 1).ToString(),
            StartDate = new DateOnly(currentYearNumber - 1, 1, 1),
            EndDate = new DateOnly(currentYearNumber - 1, 12, 31),
            IsClosed = false
        };
        var currentFiscalYear = new FiscalYear
        {
            OrganisationId = org.Id,
            Name = currentYearNumber.ToString(),
            StartDate = new DateOnly(currentYearNumber, 1, 1),
            EndDate = new DateOnly(currentYearNumber, 12, 31),
            IsClosed = false
        };
        db.FiscalYears.AddRange(previousFiscalYear, currentFiscalYear);
        await db.SaveChangesAsync();

        await new BasImportService(db).ImportDefaultAsync(previousFiscalYear.Id);
        await new BasImportService(db).ImportDefaultAsync(currentFiscalYear.Id);

        await SeedPreviousYearEntriesAsync(db, previousFiscalYear);
        await SeedCurrentYearEntriesAsync(db, currentFiscalYear.Id);
    }

    private static async Task<Dictionary<string, Account>> LoadDemoAccountsAsync(AppDbContext db, int fiscalYearId)
    {
        return await db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Where(a => a.AccountNumber == "1910" || a.AccountNumber == "2440"
                || a.AccountNumber == "2081" || a.AccountNumber == "3001" || a.AccountNumber == "5010")
            .ToDictionaryAsync(a => a.AccountNumber);
    }

    private static async Task PostEntryAsync(
        JournalEntryService journalEntryService, int fiscalYearId, DateOnly date,
        int debitAccountId, int creditAccountId, decimal amount, string description)
    {
        var entry = new JournalEntry
        {
            Date = date,
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

        var postError = await journalEntryService.PostAsync(created!.Id);
        if (postError is not null)
            throw new InvalidOperationException($"Demo seed failed to post journal entry '{description}': {postError}");
    }

    private static async Task SeedPreviousYearEntriesAsync(AppDbContext db, FiscalYear previousFiscalYear)
    {
        var accounts = await LoadDemoAccountsAsync(db, previousFiscalYear.Id);
        var cash = accounts["1910"].Id;
        var payables = accounts["2440"].Id;
        var revenue = accounts["3001"].Id;
        var expense = accounts["5010"].Id;

        var year = previousFiscalYear.StartDate.Year;
        var journalEntryService = new JournalEntryService(db);

        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 2, 10), cash, revenue, 9000m, "Kontantförsäljning");
        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 5, 15), expense, cash, 8000m, "Lokalhyra");
        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 8, 20), cash, revenue, 11000m, "Kontantförsäljning");
        await PostEntryAsync(journalEntryService, previousFiscalYear.Id, new DateOnly(year, 11, 5), expense, payables, 4000m, "Inköp material");

        previousFiscalYear.IsClosed = true;
        previousFiscalYear.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task SeedCurrentYearEntriesAsync(AppDbContext db, int fiscalYearId)
    {
        var accounts = await LoadDemoAccountsAsync(db, fiscalYearId);
        var cash = accounts["1910"].Id;
        var payables = accounts["2440"].Id;
        var equity = accounts["2081"].Id;
        var revenue = accounts["3001"].Id;
        var expense = accounts["5010"].Id;

        var year = DateTime.UtcNow.Year;
        var journalEntryService = new JournalEntryService(db);

        (DateOnly Date, int DebitAccountId, int CreditAccountId, decimal Amount, string Description)[] entries =
        [
            (new DateOnly(year, 1, 5), cash, equity, 50000m, "Aktiekapital"),
            (new DateOnly(year, 2, 10), cash, revenue, 12000m, "Kontantförsäljning"),
            (new DateOnly(year, 3, 1), expense, cash, 8000m, "Lokalhyra"),
            (new DateOnly(year, 4, 15), cash, revenue, 15000m, "Kontantförsäljning"),
            (new DateOnly(year, 5, 20), payables, cash, 3000m, "Betalning leverantörsfaktura"),
            (new DateOnly(year, 6, 10), expense, payables, 4500m, "Inköp material")
        ];

        foreach (var (date, debitAccountId, creditAccountId, amount, description) in entries)
        {
            await PostEntryAsync(journalEntryService, fiscalYearId, date, debitAccountId, creditAccountId, amount, description);
        }

        // Leave voucher #3 as a gap: bypass JournalEntryService (which blocks deleting posted
        // entries) to simulate a historical direct-DB deletion — the exact scenario BFNAR 2013:2
        // gap detection exists to catch. Entry numbers are assigned sequentially by
        // JournalEntryService.CreateAsync in the order entries were posted above, so re-querying
        // ordered by EntryNumber and taking the 3rd one identifies the entry to delete.
        var postedEntryIds = await db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .OrderBy(j => j.EntryNumber)
            .Select(j => j.Id)
            .ToListAsync();

        var gapEntry = await db.JournalEntries
            .Include(j => j.Lines)
            .FirstAsync(j => j.Id == postedEntryIds[2]);
        db.JournalEntryLines.RemoveRange(gapEntry.Lines);
        db.JournalEntries.Remove(gapEntry);
        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter DemoDataSeederTests`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Application/Services/DemoDataSeeder.cs tests/KoalaBooks.Tests/DemoDataSeederTests.cs
git commit -m "feat: seed journal entries across both years with a voucher gap in the current year"
```

---

### Task 4: Wire `DemoDataSeeder` into `Program.cs`

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs:3-4` (usings), `src/KoalaBooks.Web/Program.cs:216-237` (seed block)

**Interfaces:**
- Consumes: `DemoDataSeeder.SeedAsync(IServiceProvider)` from Tasks 1-3.

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
Expected: PASS — all existing tests (including `Api`/`Oidc`/`TenantIsolation` tests, which run in `Testing` environment and never reach this seed block) continue to pass, plus the 7 `DemoDataSeederTests`.

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
3. Confirm the "Demo AB" organisation is active, both fiscal years exist (previous year closed, current year open) each with a full BAS chart of accounts, and the current year's journal shows 5 posted entries spread across Jan–Jun with voucher number 3 visibly skipped.
4. On the journal page (PR #157's fiscal-year/month filter, once merged), confirm switching to the previous (closed) fiscal year hides the "Ny verifikation" button and shows its 4 entries, and that the month dropdown filters correctly in both years.
5. If the separate voucher-gap-detection feature (PR #178) has since merged, also confirm entry #3 in the current year shows up there as an unexplained gap — this seed doesn't depend on that feature, but is designed to exercise it.
