# BAS 2026 Seed & Account Balance Mapping

**Date:** 2026-05-30  
**Status:** Approved for implementation

## Overview

Two related features:

1. **BAS 2026 seed** — checkbox on fiscal year creation that imports the embedded BAS 2026 kontoplan (1 282 accounts) into the new year.
2. **Account balance mapping tool** — a standalone UI to map UBs from one fiscal year into IBs on another, including cross-account remapping (e.g., 1241→1226 when switching from BAS 2025 to 2026). Works in any direction (forward or backward).

The existing auto-copy-on-creation behavior is kept unchanged. The mapping tool is the escape hatch for when accounts change between years or balances are imported retroactively via SIE.

---

## 1. BAS 2026 Seed

### Embedded resource

Copy `BAS_kontoplan_2026_v2.xlsx` into `src/KoalaBooks.Infrastructure/Resources/`. Add to `KoalaBooks.Infrastructure.csproj` as `EmbeddedResource`.

### `BasImportService.ImportDefaultAsync`

```csharp
public async Task<BasImportResult> ImportDefaultAsync(int fiscalYearId)
```

Opens the manifest resource stream for `BAS_kontoplan_2026_v2.xlsx` and delegates to the existing `ImportFromExcelAsync`. No new parsing logic.

### UI — `FiscalYears.razor`

Add a checkbox below the date fields in the creation form:

```
☐ Importera BAS 2026 kontoplan
```

Unchecked by default. After `FiscalYearService.CreateAsync` succeeds, if checked, call `BasImportService.ImportDefaultAsync(newFy.Id)`. Show a combined snackbar: "Räkenskapsår skapat. Importerade X konton från BAS 2026."

No other changes to the creation flow.

---

## 2. Account Balance Mapping Tool

### Purpose

Let the user take non-zero UBs from a **source year** and write them as IBs on accounts in a **target year**, with optional account-to-account remapping. Replaces any previously applied mapping on the target year (warn + confirm first).

### Data model

Add `PreviousFiscalYearId` (nullable `int`, FK to `FiscalYear`) to the `FiscalYear` entity. Represents which year's UBs were used to populate this year's IBs. Requires a migration.

The auto-copy in `FiscalYearService.CreateAsync` sets this field when it copies from a previous year.

### New page — `/account-mapping`

Add to nav menu. Single page with three states:

**State 1 — Year picker**

Two dropdowns: Source year and Target year. Both show all fiscal years. User cannot pick the same year for both. A "Nästa" button proceeds to the mapping table.

If the target year already has `PreviousFiscalYearId` set, show an inline warning before proceeding:  
_"År [target] har redan mappats från [previous source]. Att fortsätta skriver över befintliga ingående saldon."_  
User must confirm to proceed.

**State 2 — Mapping table**

Shows all accounts from the **source year** with a non-zero UB. Columns:

| Konto (källa) | Namn (källa) | UB | → | Konto (mål) | Namn (mål) |
|---|---|---|---|---|---|

The **target account** column is a searchable dropdown listing all accounts in the target year. Pre-populated automatically:
- If the same account number exists in the target year → pre-select it.
- If not → left empty (user must choose or leave blank to skip).

Accounts left blank are skipped (no IB written). A summary at the bottom shows how many accounts will be mapped and how many skipped.

A "Tillämpa" button applies the mapping. A "Avbryt" button returns to State 1.

**State 3 — Result**

After applying: show count of accounts mapped, accounts skipped, total IB written. "Stäng" returns to State 1.

### `AccountMappingService` (new, in Application layer)

```csharp
public record MappingRow(
    string SourceAccountNumber,
    string SourceAccountName,
    decimal Ub,
    string? TargetAccountNumber);   // null = skip

public record ApplyMappingResult(int Mapped, int Skipped);

public Task<List<MappingRow>> BuildMappingAsync(int sourceFiscalYearId, int targetFiscalYearId);
public Task<ApplyMappingResult> ApplyMappingAsync(
    int sourceFiscalYearId,
    int targetFiscalYearId,
    List<MappingRow> rows);         // rows with null TargetAccountNumber are skipped
```

`ApplyMappingAsync`:
1. Validates source and target years belong to the tenant.
2. For each row with a non-null target account: sets `IncomingBalance` on the target account.
3. Sets `PreviousFiscalYearId = sourceFiscalYearId` on the target `FiscalYear`.
4. Saves in a single `SaveChangesAsync`.

Does **not** clear previously unmapped IBs — it only writes to accounts explicitly included in the mapping. This lets the user apply partial mappings incrementally.

### Auto-propagation

`OutgoingBalance` is only ever written in two places — year-end closing and SIE import — so no journal-entry-level hook is needed.

**Year-end closing** already calls `FiscalYearService.PropagateBalancesToNextYearAsync` as its final step. Update that method to look up the following year via `PreviousFiscalYearId` (a year where `PreviousFiscalYearId = closedYearId`) in addition to the current date-based fallback. This means: close 2025 → IBs on 2026 update automatically if the link exists.

**SIE import** sets UBs but does not propagate. Add a call to `PropagateBalancesToNextYearAsync` after each fiscal year is imported and balances are written. This means: import 2025 SIE → IBs on 2026 update automatically if the link exists.

**Mapping tool** calls `PropagateBalancesToNextYearAsync` on the source year after applying, so any year linked to the source also stays in sync.

### Auto-copy update

`FiscalYearService.CopyAccountsFromPreviousYearAsync` should set `PreviousFiscalYearId` on the target year after copying, so the mapping tool can detect and warn about it.

---

## 3. Navigation

Add "Kontobalansöverföring" (or similar short label) to the nav menu pointing to `/account-mapping`.

---

## 4. Testing

- `BasImportService`: `ImportDefaultAsync_ImportsAccounts` — calls `ImportDefaultAsync` on a test fiscal year, asserts `ImportedCount > 0` and no errors.
- `AccountMappingService`: 
  - `BuildMapping_PreSelectsSameAccountNumber` — source and target both have account 1910; result row has TargetAccountNumber = "1910".
  - `BuildMapping_LeavesBlank_WhenTargetMissing` — source has account 1241, target does not; result row has null TargetAccountNumber.
  - `ApplyMapping_WritesIbToTargetAccounts` — applies a two-row mapping, asserts IBs set correctly.
  - `ApplyMapping_SetsPreviousFiscalYearId` — after apply, target year's PreviousFiscalYearId matches source year.
  - `ApplyMapping_SkipsNullTargetRows` — rows with null target are not written.
- `FiscalYearService`: `PropagateBalances_FollowsPreviousFiscalYearIdLink` — two years linked via `PreviousFiscalYearId`; propagation updates the correct year even when date ordering would pick a different one.
- `SieImportService`: `ImportFiscalYear_PropagatesBalancesToLinkedNextYear` — importing a year with UBs triggers IB update on the linked following year.

---

## Out of scope

- Splitting one source UB across multiple target accounts.
- Mapping tool for journal entries (only balances are mapped here).
- Automatic detection of BAS year-to-year account renames.
