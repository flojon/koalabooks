# Remove JournalEntry.IsPosted, Use Status Everywhere Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Follow-up to `docs/superpowers/plans/2026-07-01-journal-entry-storno-compliance.md` — finish the migration off `bool IsPosted` by moving every remaining reader/writer onto `JournalEntryStatus`, then delete the `IsPosted` column entirely.

**Prerequisite:** This plan assumes `docs/superpowers/plans/2026-07-01-journal-entry-storno-compliance.md` has already been executed and merged, so `JournalEntry.Status` (`JournalEntryStatus.Draft/Posted/Reversed/Correction`) and `JournalEntry.SourceJournalEntryId` already exist, `PostAsync` already sets `Status = Posted`, and `CreateReversalAsync` already sets the reversal's `Status = Correction` and the original's `Status = Reversed`. `IsPosted` is still present on the entity and still being read/written everywhere else — that's exactly what this plan removes.

**Architecture:** `IsPosted` stays on the `JournalEntry` entity, unused, through Tasks 1–4 while every call site is moved onto `Status` layer by layer (Application → Infrastructure → Web → Blazor UI), so every intermediate task compiles and the full test suite passes. Task 5 deletes the property, its EF mapping implications, and generates the column-drop migration once nothing references it. Since this is a pure refactor (no new behavior), each task's test step is "run the existing suite, confirm no regressions" rather than a new red/green cycle — the mechanical edit and its test-fixture updates land together in one step because `Status != Draft` and `IsPosted == true` are equivalent for every entry that exists today, so there is nothing new to assert, only existing assertions to re-express.

**Semantic mapping used throughout:**
- `entry.IsPosted` (read, "is this posted/immutable") → `entry.Status != JournalEntryStatus.Draft`
- `!entry.IsPosted` (read, "is this still a draft") → `entry.Status == JournalEntryStatus.Draft`
- `entry.IsPosted = true` (write, on a normal post or a generated posting entry) → `entry.Status = JournalEntryStatus.Posted`
- The one exception: the reversal entry created in `CreateReversalAsync` already gets `Status = JournalEntryStatus.Correction` per the prerequisite plan, not `Posted` — its `IsPosted = true` line is simply deleted, nothing added.

## Global Constraints

- `CustomerInvoice.IsPosted` (`src/KoalaBooks.Domain/Entities/CustomerInvoice.cs:31`) and its usages in `CustomerInvoiceService.cs` and `CustomerInvoices.razor` are a **different, unrelated entity's own flag** — do not touch them. Only `JournalEntry.IsPosted` is in scope.
- `SupplierInvoice` has no `IsPosted` property of its own — every `IsPosted` reference inside `SupplierInvoiceService.cs` is actually on a `JournalEntry` it constructs or queries, and is in scope.
- Every file touched in this plan already has `using KoalaBooks.Domain.Enums;` at the top (verified) — no new `using` directives are needed anywhere.
- Target framework `net10.0`, Postgres via Aspire/Npgsql in dev, Testcontainers.PostgreSql in tests — same as the prerequisite plan's constraints.
- Enum values on the REST API are serialized as strings via `[property: JsonConverter(typeof(JsonStringEnumConverter))]` (already applied to `JournalEntryResponse.Status` by the prerequisite plan) — nothing new to configure here.

---

### Task 1: Application layer — `JournalEntryService`, `YearEndClosingService`, `AccountMappingService`, `SupplierInvoiceService`

**Files:**
- Modify: `src/KoalaBooks.Application/Services/JournalEntryService.cs`
- Modify: `src/KoalaBooks.Application/Services/YearEndClosingService.cs`
- Modify: `src/KoalaBooks.Application/Services/AccountMappingService.cs`
- Modify: `src/KoalaBooks.Application/Services/SupplierInvoiceService.cs`
- Modify tests: `tests/KoalaBooks.Tests/AuditTrailTests.cs`, `PostFiscalYearGuardTests.cs`, `YearEndClosingServiceTests.cs`, `ReversalClosedYearTests.cs`, `BalanceSheetTests.cs`, `BookkeepingTests.cs`, `GetAccountIdsWithTransactionsTests.cs`, `IncomeStatementTests.cs`, `TenantIsolationTests.cs`, `GeneralLedgerTests.cs`, `VatReportTests.cs`, `DraftFilteringTests.cs`

**Interfaces:**
- Consumes: `JournalEntryStatus` enum, `JournalEntry.Status` (from the prerequisite plan).
- Produces: none of these methods' signatures change — this task only changes what they read/write internally. Later tasks (2, 3, 4) still call `PostAsync`, `CreateReversalAsync`, `DeleteDraftAsync`, `GetByFiscalYearAsync`, etc. exactly as before.

- [ ] **Step 1: Confirm the baseline is green**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS (this is the prerequisite plan's end state — confirm it before changing anything).

- [ ] **Step 2: Edit `JournalEntryService.cs` — guard checks and assignments**

In `src/KoalaBooks.Application/Services/JournalEntryService.cs`:

Change (in `UpdateAsync`):
```csharp
        if (existing.IsPosted)
            return (null, "Cannot modify a posted journal entry. Create a reversal instead.");
```
to:
```csharp
        if (existing.Status != JournalEntryStatus.Draft)
            return (null, "Cannot modify a posted journal entry. Create a reversal instead.");
```

Change (in `PostAsync`):
```csharp
        if (entry.IsPosted)
            return "Journal entry is already posted.";
        if (entry.FiscalYear.IsClosed)
            return "Cannot post entries in a closed fiscal year.";

        entry.IsPosted = true;
        entry.Status = JournalEntryStatus.Posted;
        await _db.SaveChangesAsync();
```
to:
```csharp
        if (entry.Status != JournalEntryStatus.Draft)
            return "Journal entry is already posted.";
        if (entry.FiscalYear.IsClosed)
            return "Cannot post entries in a closed fiscal year.";

        entry.Status = JournalEntryStatus.Posted;
        await _db.SaveChangesAsync();
```

Change (in `DeleteDraftAsync`):
```csharp
        if (entry.IsPosted)
            return "Cannot delete a posted journal entry.";
```
to:
```csharp
        if (entry.Status != JournalEntryStatus.Draft)
            return "Cannot delete a posted journal entry.";
```

Change (in `CreateReversalAsync`):
```csharp
        if (original is null)
            return (null, "Journal entry not found.");
        if (!original.IsPosted)
            return (null, "Can only reverse posted entries.");
        if (original.Status == JournalEntryStatus.Reversed)
            return (null, "Journal entry has already been reversed.");
```
to:
```csharp
        if (original is null)
            return (null, "Journal entry not found.");
        if (original.Status == JournalEntryStatus.Draft)
            return (null, "Can only reverse posted entries.");
        if (original.Status == JournalEntryStatus.Reversed)
            return (null, "Journal entry has already been reversed.");
```

And, further down in the same method:
```csharp
            CreatedAt = DateTime.UtcNow,
            IsPosted = true,
            Status = JournalEntryStatus.Correction,
            SourceJournalEntryId = original.Id,
```
to:
```csharp
            CreatedAt = DateTime.UtcNow,
            Status = JournalEntryStatus.Correction,
            SourceJournalEntryId = original.Id,
```

- [ ] **Step 3: Edit `JournalEntryService.cs` — report query filters**

There are 12 remaining `IsPosted` reads in this file, all read-only query filters meaning "include only entries that are part of the permanent ledger" (`grep -n "IsPosted" src/KoalaBooks.Application/Services/JournalEntryService.cs` after Step 2 shows exactly these 12 lines: 216, 274, 343, 425, 444, 470, 493, 569, 653, 710, 765, 771). Apply two substring replacements — each is a `replace_all` on an exact substring, so it doesn't matter that some of these lines end with `;` and others continue the fluent chain onto the next `.Where(...)`, or that some have extra conditions before the `&&`:

**Replacement A** — 8 occurrences (lines 216, 274, 343, 470, 493, 569, 653, 710) are exactly the standalone call `.Where(l => l.JournalEntry.IsPosted)`, optionally followed by `;`. Using `replace_all`, change every occurrence of the substring:
```csharp
.Where(l => l.JournalEntry.IsPosted)
```
to:
```csharp
.Where(l => l.JournalEntry.Status != JournalEntryStatus.Draft)
```
(this substring match does not include the trailing `;` or lack thereof, so it correctly rewrites all 8 regardless of what follows on the line).

**Replacement B** — the remaining 4 occurrences (lines 425, 444, 765, 771) have `l.JournalEntry.IsPosted` combined with another condition via `&&` on the same `.Where(...)` — e.g. `.Where(l => l.JournalEntry.FiscalYearId == prevFy.Id && l.JournalEntry.IsPosted)` and `.Where(l => accountIdList.Contains(l.AccountId) && l.JournalEntry.IsPosted)`. Using `replace_all`, change every occurrence of the substring:
```csharp
&& l.JournalEntry.IsPosted)
```
to:
```csharp
&& l.JournalEntry.Status != JournalEntryStatus.Draft)
```

After both replacements, `grep -n "IsPosted" src/KoalaBooks.Application/Services/JournalEntryService.cs` should return no matches.

- [ ] **Step 4: Edit `YearEndClosingService.cs`**

Change (in `ValidateForClosingAsync`):
```csharp
        var draftCount = await _db.JournalEntries
            .CountAsync(j => j.FiscalYearId == fiscalYearId && !j.IsPosted);
```
to:
```csharp
        var draftCount = await _db.JournalEntries
            .CountAsync(j => j.FiscalYearId == fiscalYearId && j.Status == JournalEntryStatus.Draft);
```

Change (the transaction-totals query, appears twice — once in the main closing method, once in `GetPnLAccountBalancesAsync`):
```csharp
                .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId && l.JournalEntry.IsPosted)
```
to:
```csharp
                .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId && l.JournalEntry.Status != JournalEntryStatus.Draft)
```
and
```csharp
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId && l.JournalEntry.IsPosted)
```
to:
```csharp
            .Where(l => l.JournalEntry.FiscalYearId == fiscalYearId && l.JournalEntry.Status != JournalEntryStatus.Draft)
```
(match indentation exactly as it appears in each of the two call sites).

Change (closing entry 1 construction):
```csharp
                    Description = $"Bokslut: Resultatdisposition {fiscalYear.Name}",
                    CreatedAt = DateTime.UtcNow,
                    IsPosted = true,
                    IsClosingEntry = true,
```
to:
```csharp
                    Description = $"Bokslut: Resultatdisposition {fiscalYear.Name}",
                    CreatedAt = DateTime.UtcNow,
                    Status = JournalEntryStatus.Posted,
                    IsClosingEntry = true,
```

Change (closing entry 2 construction):
```csharp
                    Description = $"Bokslut: Årets resultat till eget kapital {fiscalYear.Name}",
                    CreatedAt = DateTime.UtcNow,
                    IsPosted = true,
                    IsClosingEntry = true,
```
to:
```csharp
                    Description = $"Bokslut: Årets resultat till eget kapital {fiscalYear.Name}",
                    CreatedAt = DateTime.UtcNow,
                    Status = JournalEntryStatus.Posted,
                    IsClosingEntry = true,
```

- [ ] **Step 5: Edit `AccountMappingService.cs`**

Both occurrences:
```csharp
                .Where(l => sourceAccountIds.Contains(l.AccountId) && l.JournalEntry.IsPosted)
```
become:
```csharp
                .Where(l => sourceAccountIds.Contains(l.AccountId) && l.JournalEntry.Status != JournalEntryStatus.Draft)
```

- [ ] **Step 6: Edit `SupplierInvoiceService.cs`**

Change (invoice posting journal entry):
```csharp
            Description = $"Leverantörsfaktura {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
            FiscalYearId = invoice.FiscalYearId,
            IsPosted = true,
            CreatedAt = DateTime.UtcNow,
            Lines = lines
```
to:
```csharp
            Description = $"Leverantörsfaktura {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
            FiscalYearId = invoice.FiscalYearId,
            Status = JournalEntryStatus.Posted,
            CreatedAt = DateTime.UtcNow,
            Lines = lines
```

Change (payment journal entry):
```csharp
            Description = $"Betalning {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
            FiscalYearId = invoice.FiscalYearId,
            IsPosted = true,
            CreatedAt = DateTime.UtcNow,
```
to:
```csharp
            Description = $"Betalning {invoice.SupplierName}" + (invoice.InvoiceNumber is not null ? $" #{invoice.InvoiceNumber}" : ""),
            FiscalYearId = invoice.FiscalYearId,
            Status = JournalEntryStatus.Posted,
            CreatedAt = DateTime.UtcNow,
```

Change (`GetLinkableEntriesAsync`):
```csharp
            .Where(j => j.FiscalYearId == fiscalYearId && j.IsPosted && !j.IsClosingEntry)
```
to:
```csharp
            .Where(j => j.FiscalYearId == fiscalYearId && j.Status != JournalEntryStatus.Draft && !j.IsClosingEntry)
```

- [ ] **Step 7: Update dependent test fixtures/assertions**

In `tests/KoalaBooks.Tests/AuditTrailTests.cs`:
```csharp
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));
        Assert.False(entry!.IsPosted);
```
to:
```csharp
        var (entry, _) = await _f.JournalEntryService.CreateAsync(MakeEntry(1000));
        Assert.Equal(JournalEntryStatus.Draft, entry!.Status);
```
and:
```csharp
        var reloaded = await _f.Db.JournalEntries.FindAsync(entry.Id);
        Assert.True(reloaded!.IsPosted);
```
to:
```csharp
        var reloaded = await _f.Db.JournalEntries.FindAsync(entry.Id);
        Assert.Equal(JournalEntryStatus.Posted, reloaded!.Status);
```
and:
```csharp
        Assert.NotNull(reversal);
        Assert.True(reversal.IsPosted);
```
to:
```csharp
        Assert.NotNull(reversal);
        Assert.Equal(JournalEntryStatus.Correction, reversal.Status);
```

In `tests/KoalaBooks.Tests/PostFiscalYearGuardTests.cs`:
```csharp
        Assert.NotNull(created);
        Assert.False(created.IsPosted);
```
to:
```csharp
        Assert.NotNull(created);
        Assert.Equal(JournalEntryStatus.Draft, created.Status);
```

In `tests/KoalaBooks.Tests/YearEndClosingServiceTests.cs`:
```csharp
        Assert.All(closingEntries, e =>
        {
            Assert.True(e.IsPosted);
            Assert.True(e.IsClosingEntry);
```
to:
```csharp
        Assert.All(closingEntries, e =>
        {
            Assert.Equal(JournalEntryStatus.Posted, e.Status);
            Assert.True(e.IsClosingEntry);
```

In `tests/KoalaBooks.Tests/ReversalClosedYearTests.cs`:
```csharp
        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.True(reversal.IsPosted);
        Assert.Contains("Reversal", reversal.Description);
```
to:
```csharp
        Assert.Null(error);
        Assert.NotNull(reversal);
        Assert.Equal(JournalEntryStatus.Correction, reversal.Status);
        Assert.Contains("Reversal", reversal.Description);
```

In each of `tests/KoalaBooks.Tests/BalanceSheetTests.cs`, `BookkeepingTests.cs`, `GetAccountIdsWithTransactionsTests.cs`, `IncomeStatementTests.cs`, `TenantIsolationTests.cs`, `GeneralLedgerTests.cs`, `VatReportTests.cs`, change the single object-initializer line:
```csharp
            IsPosted = true,
```
to:
```csharp
            Status = JournalEntryStatus.Posted,
```
(in `GetAccountIdsWithTransactionsTests.cs` and `VatReportTests.cs` this line is immediately followed by `IsClosingEntry = true,` — leave that line untouched, only replace the `IsPosted` line.)

In `tests/KoalaBooks.Tests/DraftFilteringTests.cs`, update the stale comment:
```csharp
/// include ALL entries regardless of IsPosted status.
```
to:
```csharp
/// include ALL entries regardless of Status.
```

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, same test count as the Step 1 baseline. `grep -rn "IsPosted" src/KoalaBooks.Application tests/KoalaBooks.Tests` should now only show hits in `CustomerInvoiceService.cs`-adjacent files (`CustomerInvoiceService.cs` itself isn't in `src/KoalaBooks.Application/Services`... actually it is — confirm the only remaining `Application`-layer hits are `CustomerInvoiceService.cs`'s own `CustomerInvoice.IsPosted` usages, which are out of scope and must remain).

- [ ] **Step 9: Commit**

```bash
git add src/KoalaBooks.Application/Services/JournalEntryService.cs \
        src/KoalaBooks.Application/Services/YearEndClosingService.cs \
        src/KoalaBooks.Application/Services/AccountMappingService.cs \
        src/KoalaBooks.Application/Services/SupplierInvoiceService.cs \
        tests/KoalaBooks.Tests/AuditTrailTests.cs \
        tests/KoalaBooks.Tests/PostFiscalYearGuardTests.cs \
        tests/KoalaBooks.Tests/YearEndClosingServiceTests.cs \
        tests/KoalaBooks.Tests/ReversalClosedYearTests.cs \
        tests/KoalaBooks.Tests/BalanceSheetTests.cs \
        tests/KoalaBooks.Tests/BookkeepingTests.cs \
        tests/KoalaBooks.Tests/GetAccountIdsWithTransactionsTests.cs \
        tests/KoalaBooks.Tests/IncomeStatementTests.cs \
        tests/KoalaBooks.Tests/TenantIsolationTests.cs \
        tests/KoalaBooks.Tests/GeneralLedgerTests.cs \
        tests/KoalaBooks.Tests/VatReportTests.cs \
        tests/KoalaBooks.Tests/DraftFilteringTests.cs
git commit -m "refactor: move JournalEntryService/YearEndClosingService/AccountMappingService/SupplierInvoiceService off IsPosted onto Status"
```

---

### Task 2: Infrastructure layer — `SieExportService`, `SieImportService`, `BankImportService`

**Files:**
- Modify: `src/KoalaBooks.Infrastructure/Services/SieExportService.cs`
- Modify: `src/KoalaBooks.Infrastructure/Services/SieImportService.cs`
- Modify: `src/KoalaBooks.Infrastructure/Services/BankImportService.cs`
- Modify test: `tests/KoalaBooks.Tests/SieExportTests.cs`

**Interfaces:**
- Consumes: same `JournalEntryStatus` semantics as Task 1. No dependency on Task 1's specific edits (different files, no shared code path), so this task can run before or after Task 1, but must run before Task 5 (which deletes the property).

- [ ] **Step 1: Edit `SieExportService.cs`**

```csharp
        foreach (var entry in fiscalYear.JournalEntries.Where(e => e.IsPosted).OrderBy(e => e.EntryNumber))
```
to:
```csharp
        foreach (var entry in fiscalYear.JournalEntries.Where(e => e.Status != JournalEntryStatus.Draft).OrderBy(e => e.EntryNumber))
```

- [ ] **Step 2: Edit `SieImportService.cs`**

```csharp
                FiscalYearId = fiscalYear.Id,
                CreatedAt = DateTime.UtcNow,
                IsPosted = true,
                Lines = []
```
to:
```csharp
                FiscalYearId = fiscalYear.Id,
                CreatedAt = DateTime.UtcNow,
                Status = JournalEntryStatus.Posted,
                Lines = []
```

- [ ] **Step 3: Edit `BankImportService.cs`**

```csharp
            .Where(j => j.FiscalYearId == fiscalYearId && j.IsPosted && !j.IsClosingEntry)
```
to:
```csharp
            .Where(j => j.FiscalYearId == fiscalYearId && j.Status != JournalEntryStatus.Draft && !j.IsClosingEntry)
```

- [ ] **Step 4: Update `SieExportTests.cs`**

```csharp
            Date = date,
            Description = description,
            FiscalYearId = fiscalYearId,
            IsPosted = true,
        };
```
to:
```csharp
            Date = date,
            Description = description,
            FiscalYearId = fiscalYearId,
            Status = JournalEntryStatus.Posted,
        };
```

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS. `grep -rn "IsPosted" src/KoalaBooks.Infrastructure` should now return no matches.

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Infrastructure/Services/SieExportService.cs \
        src/KoalaBooks.Infrastructure/Services/SieImportService.cs \
        src/KoalaBooks.Infrastructure/Services/BankImportService.cs \
        tests/KoalaBooks.Tests/SieExportTests.cs
git commit -m "refactor: move SieExportService/SieImportService/BankImportService off IsPosted onto Status"
```

---

### Task 3: Web layer — `JournalEntryResponse`, `JournalEntriesController`

**Files:**
- Modify: `src/KoalaBooks.Web/Models/Api/JournalEntryResponse.cs`
- Modify: `src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs`

**Interfaces:**
- Produces: `JournalEntryResponse` no longer has an `IsPosted` field — `Status` (already added by the prerequisite plan) is the only status field exposed over the API from now on. No test in `ApiTests.cs` asserts on an `isPosted` JSON property (confirmed by grep), so no test changes are needed in this task.

- [ ] **Step 1: Edit `JournalEntryResponse.cs`**

```csharp
public record JournalEntryResponse(
    int Id,
    int EntryNumber,
    DateOnly Date,
    string Description,
    bool IsPosted,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] JournalEntryStatus Status,
    int? SourceJournalEntryId,
    DateTime CreatedAt,
    List<JournalEntryLineResponse> Lines);
```
to:
```csharp
public record JournalEntryResponse(
    int Id,
    int EntryNumber,
    DateOnly Date,
    string Description,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] JournalEntryStatus Status,
    int? SourceJournalEntryId,
    DateTime CreatedAt,
    List<JournalEntryLineResponse> Lines);
```

- [ ] **Step 2: Edit `JournalEntriesController.cs`**

```csharp
    private static JournalEntryResponse MapEntry(JournalEntry e) =>
        new(e.Id, e.EntryNumber, e.Date, e.Description, e.IsPosted, e.Status, e.SourceJournalEntryId, e.CreatedAt,
            e.Lines.Select(l => new JournalEntryLineResponse(
                l.Id, l.AccountId,
                l.Account?.AccountNumber ?? "",
                l.Account?.Name ?? "",
                l.DebitAmount, l.CreditAmount)).ToList());
```
to:
```csharp
    private static JournalEntryResponse MapEntry(JournalEntry e) =>
        new(e.Id, e.EntryNumber, e.Date, e.Description, e.Status, e.SourceJournalEntryId, e.CreatedAt,
            e.Lines.Select(l => new JournalEntryLineResponse(
                l.Id, l.AccountId,
                l.Account?.AccountNumber ?? "",
                l.Account?.Name ?? "",
                l.DebitAmount, l.CreditAmount)).ToList());
```

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS, including every `ApiTests.JournalEntries_*` test.

- [ ] **Step 4: Commit**

```bash
git add src/KoalaBooks.Web/Models/Api/JournalEntryResponse.cs \
        src/KoalaBooks.Web/Controllers/Api/JournalEntriesController.cs
git commit -m "refactor: drop IsPosted from the journal entry API response"
```

---

### Task 4: Blazor UI — `Journal.razor`

**Files:**
- Modify: `src/KoalaBooks.Components/Pages/Journal.razor`

**Interfaces:**
- Consumes: `JournalEntry.Status` only (no more `IsPosted`). No automated test coverage exists for this file (no bUnit in the test project) — verified manually.

- [ ] **Step 1: Edit the `canConvert` computation**

```razor
            var canConvert = entry.IsPosted
                && !entry.IsClosingEntry
                && !_linkedJournalEntryIds.Contains(entry.Id)
                && entry.Lines.Any(l => l.CreditAmount > 0 && l.Account?.AccountNumber?.StartsWith("24") == true);
```
to:
```razor
            var canConvert = entry.Status != JournalEntryStatus.Draft
                && !entry.IsClosingEntry
                && !_linkedJournalEntryIds.Contains(entry.Id)
                && entry.Lines.Any(l => l.CreditAmount > 0 && l.Account?.AccountNumber?.StartsWith("24") == true);
```

- [ ] **Step 2: Edit the status badge's final branch**

```razor
                    else if (entry.IsPosted)
                    {
                        <span style="color:#16a34a;">✅ Bokförd</span>
                    }
                    else
                    {
                        <span style="color:#f59e0b;">📝 Utkast</span>
                    }
                </td>
```
to:
```razor
                    else if (entry.Status == JournalEntryStatus.Posted)
                    {
                        <span style="color:#16a34a;">✅ Bokförd</span>
                    }
                    else
                    {
                        <span style="color:#f59e0b;">📝 Utkast</span>
                    }
                </td>
```

- [ ] **Step 3: Edit the reverse-button gating**

```razor
                    else if (entry.IsPosted && entry.Status != JournalEntryStatus.Reversed)
                    {
                        <button class="btn btn-sm btn-warning" @onclick="() => StartReversal(entry.Id)">Återför</button>
```
to:
```razor
                    else if (entry.Status is JournalEntryStatus.Posted or JournalEntryStatus.Correction)
                    {
                        <button class="btn btn-sm btn-warning" @onclick="() => StartReversal(entry.Id)">Återför</button>
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS (this file has no automated coverage, but a build break here would fail the build step of `dotnet test`).

- [ ] **Step 5: Manually verify in the browser**

Run (see the project's `aspire` skill for details):
```bash
cd src/KoalaBooks.AppHost
aspire run
```
Log in with `admin@koalabooks.local` / `Admin123!`, navigate to `/journal`, and repeat the golden path from the prerequisite plan's Task 5: create a draft, post it, reverse it, confirm the badges and button visibility are unchanged from before this task (this task is a pure internal refactor of how the same UI states are computed — behavior must be identical, not new).

- [ ] **Step 6: Commit**

```bash
git add src/KoalaBooks.Components/Pages/Journal.razor
git commit -m "refactor: drop IsPosted from Journal.razor status display"
```

---

### Task 5: Remove `IsPosted` from the entity and drop the column

**Files:**
- Modify: `src/KoalaBooks.Domain/Entities/JournalEntry.cs`
- Create: EF migration in `src/KoalaBooks.Infrastructure/Migrations/`

**Interfaces:**
- Produces: `JournalEntry` no longer has an `IsPosted` member. This is the last task — nothing downstream depends on it.

- [ ] **Step 1: Confirm nothing references `IsPosted` anymore**

Run: `grep -rn "\.IsPosted\|IsPosted =" src/ --include="*.cs" --include="*.razor" | grep -v "CustomerInvoice"`
Expected: only `src/KoalaBooks.Domain/Entities/JournalEntry.cs:10: public bool IsPosted { get; set; }` (the declaration itself) should remain. If anything else shows up, stop and go back to the task that owns that file — do not proceed until this grep is clean.

- [ ] **Step 2: Remove the property**

In `src/KoalaBooks.Domain/Entities/JournalEntry.cs`:
```csharp
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsPosted { get; set; }
    public bool IsClosingEntry { get; set; }
```
to:
```csharp
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsClosingEntry { get; set; }
```

- [ ] **Step 3: Generate the migration**

Run:
```bash
dotnet ef migrations add RemoveJournalEntryIsPosted \
  --project src/KoalaBooks.Infrastructure \
  --startup-project src/KoalaBooks.Web
```

Expected: new migration files appear, with `Up()` containing a single `migrationBuilder.DropColumn(name: "IsPosted", table: "JournalEntries");` and `Down()` containing the corresponding `AddColumn<bool>(name: "IsPosted", table: "JournalEntries", type: "boolean", nullable: false, defaultValue: false);`. No manual data-backfill edit is needed this time — dropping a now-unread column loses no information that matters (`Status` already carries everything `IsPosted` used to).

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: PASS — this exercises the migration path via each test's `TestFixture` → `Db.Database.EnsureCreated()`, which builds the schema from the post-removal model directly.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.Domain/Entities/JournalEntry.cs \
        src/KoalaBooks.Infrastructure/Migrations/
git commit -m "feat: remove JournalEntry.IsPosted, Status is now the sole source of truth"
```
