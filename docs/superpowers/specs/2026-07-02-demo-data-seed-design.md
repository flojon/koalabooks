# Demo Data Seed (Dev + PR Previews)

**Date:** 2026-07-02
**Status:** Draft

## Overview

Replace the current bare-bones dev seed (org + one user, no books data) with a richer demo dataset — an organisation, a login-capable user, a small chart of accounts, an open fiscal year, and a handful of posted vouchers with one deliberate gap in the voucher-number sequence. One seeder, two trigger conditions: automatically in local `Development`, and via an explicit opt-in flag in PR previews. Never in `Production`.

## Background

PR previews (`docs/superpowers/specs/2026-06-13-pr-preview-deployments-design.md`) each get a fresh, isolated Postgres database. Fresh means empty: no organisation, no login-capable user beyond what `IsDevelopment()`-gated seeding creates — and previews run `ASPNETCORE_ENVIRONMENT=Staging`, so that gate never fires. There is no self-registration flow, so a preview with no seed data is not just empty, it's unloginable. The primary use case for previews is manual click-through QA by a reviewer, which requires realistic-looking books data to actually exercise most features (e.g. voucher gap detection, journal filtering).

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
- **Fiscal year**: current calendar year, `IsClosed = false`. (Computed from `DateTime.UtcNow.Year` at seed time, not hardcoded, so the seed stays useful without edits as time passes.)
- **Chart of accounts**: the full BAS 2026 kontoplan (1 282 accounts), imported via the existing `BasImportService.ImportDefaultAsync(fiscalYearId)` — the same code path the "Importera BAS 2026 kontoplan" checkbox on fiscal year creation already uses. Reuses tested import logic instead of hand-maintaining a curated account list, and matches the real chart of accounts a reviewer would actually work with, rather than a stripped-down demo subset.
- **Journal entries**: six entries posted through `JournalEntryService.CreateAsync` + `PostAsync` (normal validated path, same pattern as `TestFixture.CreateAndPostEntryAsync`), dated across the current month, alternating simple debit/credit pairs across five familiar standard BAS accounts already present in the imported plan — 1910 Kassa, 2440 Leverantörsskulder, 2081 Aktiekapital, 3010 Försäljning, 5010 Lokalhyra (e.g. cash sale, rent payment, capital contribution). This assigns sequential `EntryNumber`s 1–6.
- **The gap**: after posting, entry #3 (and its lines) is deleted directly via `AppDbContext` — bypassing `JournalEntryService`, which by design doesn't allow deleting posted entries. This mirrors the kind of real-world scenario voucher-number-gap detection is meant to catch (a historical direct-DB deletion). The gap is created and verified purely as a fact about `JournalEntry.EntryNumber` values in the database — this seed has no dependency on the voucher-gap-detection feature itself (currently in a separate, unmerged PR), so it builds and passes standalone against `main`. If/when that feature merges, the gap will be visible through it automatically since it's just reading the same `EntryNumber` sequence.

## Code Location

New `DemoDataSeeder` class in `src/KoalaBooks.Infrastructure/Services/`, alongside the existing `AspireDashboardSeeder`, with a single entry point:

```csharp
public static async Task SeedAsync(IServiceProvider services)
```

Called from `Program.cs` in the same startup block as today's seed, replacing the inline org/user creation:

```csharp
if (app.Environment.IsDevelopment() || builder.Configuration["SEED_DEMO_DATA"] == "true")
{
    await DemoDataSeeder.SeedAsync(scope.ServiceProvider);
}
```

## Config Wiring

Add to `docker-compose.pr-preview.yml`:
```yaml
environment:
  - SEED_DEMO_DATA=true
```
No other workflow or secret changes needed — this is an unauthenticated, non-secret flag.

## Testing

- `DemoDataSeederTests` (new, using the existing `PostgresContainerFixture` pattern from `TestFixture`):
  - `SeedAsync_CreatesLoginableUser` — asserts the seed user exists and can be found by `UserManager`.
  - `SeedAsync_ImportsBasChartOfAccounts` — asserts the seeded fiscal year has a full BAS-sized chart of accounts (`Count > 1000`) and that the five accounts used for journal entries (1910, 2440, 2081, 3010, 5010) are present among them.
  - `SeedAsync_LeavesOneVoucherGap` — asserts the seeded fiscal year's `JournalEntry.EntryNumber` values are exactly `[1, 2, 4, 5, 6]` (no dependency on the separate voucher-gap-detection feature/PR).
  - `SeedAsync_IsIdempotent` — calling `SeedAsync` twice does not create a second organisation or duplicate accounts.

## Out of Scope

- Per-PR customization of seed data based on which feature the PR touches — one general-purpose baseline dataset is seeded identically for every preview and for local dev.
- Seeding documents into the document inbox — can be added later if reviewers need to QA that feature specifically.
- Resetting/re-seeding a running preview without a redeploy (e.g. an admin "reset demo data" button) — not needed today.
