# Demo Data Seed (Dev + PR Previews)

**Date:** 2026-07-02
**Status:** Implemented — see `docs/superpowers/plans/2026-07-02-demo-data-seed.md` for the as-built plan (some details below were corrected during implementation and are reflected here)

## Overview

Replace the current bare-bones dev seed (org + one user, no books data) with a richer demo dataset — an organisation, a login-capable user, a full BAS chart of accounts, two fiscal years (a closed prior year and an open current year) with posted vouchers spread across different months, and one deliberate gap in the current year's voucher-number sequence. One seeder, two trigger conditions: automatically in local `Development`, and via an explicit opt-in flag in PR previews. Never in `Production`.

## Background

PR previews (`docs/superpowers/specs/2026-06-13-pr-preview-deployments-design.md`) each get a fresh, isolated Postgres database. Fresh means empty: no organisation, no login-capable user beyond what `IsDevelopment()`-gated seeding creates — and previews run `ASPNETCORE_ENVIRONMENT=Staging`, so that gate never fires. There is no self-registration flow, so a preview with no seed data is not just empty, it's unloginable. The primary use case for previews is manual click-through QA by a reviewer, which requires realistic-looking books data to actually exercise most features. Two concrete examples in flight: voucher-number-gap detection (needs a posted voucher with a missing sequence number) and PR #157's journal fiscal-year/month filter (needs multiple fiscal years to switch between and entries spread across different months to filter on) — a single fiscal year with entries clustered in one month exercises neither well.

Separately, the existing local-dev seed in `Program.cs` only creates an org and a user — a developer still has to manually create accounts, a fiscal year, and journal entries before they can do anything useful. The same richer dataset benefits local dev too, so this seed replaces (not duplicates) that code path.

## Trigger Conditions

Seeding runs when **either**:
- `app.Environment.IsDevelopment()` — automatic, no config needed, preserves today's zero-config local dev experience.
- `builder.Configuration["SEED_DEMO_DATA"] == "true"` — explicit opt-in, set only in `docker-compose.pr-preview.yml`.

It never runs in `Production` (no `SEED_DEMO_DATA` there) and never runs in a hypothetical future real `Staging` deployment unless someone deliberately sets the flag.

Idempotency: skip entirely if the seed user (`admin@koalabooks.local`) already exists — same guard as today. This matters more now than before: PR preview containers can restart (e.g. redeploy on `synchronize` without the volume being wiped) without a fresh database, and local dev DBs persist across `dotnet run` restarts.

## Seed Content

All seeded under one organisation:

- **Organisation**: `Name = "Demo AB"`, `Slug = "demo"`, `LegalForm = LegalForm.Aktiebolag`.
- **User**: `admin@koalabooks.local` / `Admin123!` (unchanged credentials from today's dev seed), `EmailConfirmed = true`, `DisplayName = "Admin"`.
- **Two fiscal years**, both computed from `DateTime.UtcNow.Year` at seed time (not hardcoded, so the seed stays useful without edits as time passes):
  - **Previous year** (`year - 1`, full calendar year): seeded with entries, then `IsClosed = true` / `ClosedAt` set directly once seeding is done — exercises the fiscal-year switcher (PR #157) and its "new entry disabled on closed years" behavior.
  - **Current year** (`year`, full calendar year): `IsClosed = false`, the active year.
- **Chart of accounts**: the full BAS 2026 kontoplan (1 282 accounts) imported into *each* fiscal year separately via the existing `BasImportService.ImportDefaultAsync(fiscalYearId)` — the same code path the "Importera BAS 2026 kontoplan" checkbox on fiscal year creation already uses. Accounts are per-fiscal-year in this schema, so both years need their own import call. Reuses tested import logic instead of hand-maintaining a curated account list.
- **Journal entries**, posted through `JournalEntryService.CreateAsync` + `PostAsync` (normal validated path, same pattern as `TestFixture.CreateAndPostEntryAsync`), using five familiar accounts confirmed present in the imported BAS 2026 plan — 1910 Kassa, 2440 Leverantörsskulder, 2081 Aktiekapital, 3001 Försäljning inom Sverige 25% moms, 5010 Lokalhyra (account 3010, originally chosen for "revenue," does not exist in the embedded BAS 2026 file — verified directly against the imported data; 3001 is the real revenue account used instead):
  - **Previous year**: 4 entries, one per quarter-ish month (e.g. Feb, May, Aug, Nov), simple cash sale / rent / purchase pairs. No gap.
  - **Current year**: 6 entries spread one-per-month from January through June (not clustered in a single month, so PR #157's month filter has something to filter across the active year), same entries as originally designed (capital contribution, two cash sales, rent, a payables settlement, a purchase).
- **The gap**: in the *current* year only, entry #3 (and its lines) is deleted directly via `AppDbContext` after posting — bypassing `JournalEntryService`, which by design doesn't allow deleting posted entries. This mirrors the kind of real-world scenario voucher-number-gap detection is meant to catch (a historical direct-DB deletion). The gap is created and verified purely as a fact about `JournalEntry.EntryNumber` values in the database — this seed has no dependency on the voucher-gap-detection feature itself (currently in a separate, unmerged PR), so it builds and passes standalone against `main`. If/when that feature merges, the gap will be visible through it automatically since it's just reading the same `EntryNumber` sequence.

## Code Location

New `DemoDataSeeder` class in `src/KoalaBooks.Application/Services/`, with a single entry point:

```csharp
public static async Task SeedAsync(IServiceProvider services)
```

It cannot live in `KoalaBooks.Infrastructure.Services` alongside `AspireDashboardSeeder` (the original plan) — it needs both `BasImportService` (Infrastructure) and `JournalEntryService` (Application), and `Application` already project-references `Infrastructure`, so placing it in Infrastructure would require a circular reference back to Application. `KoalaBooks.Application.Services` is the only location that can see both.

Called from `Program.cs` in the same startup block as today's seed, replacing the inline org/user creation, with a hard `!IsProduction()` guard so seeding is structurally impossible in production even if `SEED_DEMO_DATA` were ever accidentally set there:

```csharp
if (!app.Environment.IsProduction() &&
    (app.Environment.IsDevelopment() || builder.Configuration["SEED_DEMO_DATA"] == "true"))
{
    await DemoDataSeeder.SeedAsync(scope.ServiceProvider);
}
```

Within `SeedAsync`, the demo user is created last — after the organisation, both fiscal years, both BAS imports, and all journal entries are committed — so the idempotency guard (`FindByEmailAsync` at the top of the method) genuinely means "fully seeded." If seeding fails partway through, the user is never created, so the next startup retries from scratch instead of getting stuck half-seeded forever.

## Config Wiring

Add to `docker-compose.pr-preview.yml`:
```yaml
environment:
  - SEED_DEMO_DATA=true
```
No other workflow or secret changes needed — this is an unauthenticated, non-secret flag.

## Testing

- `DemoDataSeederTests` (new, using the existing `PostgresContainerFixture` pattern from `TestFixture`):
  - `SeedAsync_CreatesLoginableDemoUser` — asserts the seed user exists, can be found by `UserManager`, and its password checks out.
  - `SeedAsync_CreatesTwoFiscalYears` — asserts exactly two fiscal years exist, named `(year - 1)` and `year`.
  - `SeedAsync_ImportsBasChartOfAccounts` — asserts both seeded fiscal years have a full BAS-sized chart of accounts (`Count > 1000`) and that the five accounts used for journal entries (1910, 2440, 2081, 3001, 5010) are present in both.
  - `SeedAsync_LeavesOneVoucherGapInCurrentYear` — asserts the current year's `JournalEntry.EntryNumber` values are exactly `[1, 2, 4, 5, 6]` (no dependency on the separate voucher-gap-detection feature/PR).
  - `SeedAsync_SpreadsCurrentYearEntriesAcrossMonths` — asserts the current year's posted entries span at least 5 distinct months.
  - `SeedAsync_ClosesPreviousYearWithFourEntries` — asserts one fiscal year is closed with `ClosedAt` set and has exactly 4 posted entries (no gap).
  - `SeedAsync_IsIdempotent` — calling `SeedAsync` twice does not create a second organisation.

## Out of Scope

- Per-PR customization of seed data based on which feature the PR touches — one general-purpose baseline dataset is seeded identically for every preview and for local dev.
- Seeding documents into the document inbox — can be added later if reviewers need to QA that feature specifically.
- Resetting/re-seeding a running preview without a redeploy (e.g. an admin "reset demo data" button) — not needed today.
