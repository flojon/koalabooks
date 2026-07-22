namespace KoalaBooks.Domain.Interfaces;

// Design note (program plan 5.H-2): all-or-nothing transactional semantics — the whole
// batch is created inside a single DB transaction; the first invalid entry rolls back
// everything already created in this call. Deliberately different from the
// SieImportAllResult/ZipImportResult partial-success-with-warnings convention used
// elsewhere, because this is a direct financial write (journal entries), not a
// document/reference-data import where silently skipping a bad row is acceptable.
//
// Each entry is validated the same way JournalEntryService.CreateAsync validates a single
// entry (balanced debit/credit, accounts belong to the fiscal year, date within the fiscal
// year, fiscal year open) — reused directly rather than duplicated. Validation happens
// entry-by-entry as each is created inside the transaction (fail-mid-transaction-and-
// rollback), not as a separate pre-pass: JournalEntryService.CreateAsync is the single
// source of truth for what makes an entry valid, and duplicating that logic into an
// upfront check would risk the two falling out of sync.
public record BulkJournalLineInput(int AccountId, decimal DebitAmount, decimal CreditAmount);

public record BulkJournalEntryInput(DateOnly Date, string Description, List<BulkJournalLineInput> Lines);

public record BulkJournalImportResult(bool Success, string? Error, int? FailedEntryIndex, List<int> CreatedEntryIds);

public interface IBulkJournalImportService
{
    Task<BulkJournalImportResult> ImportAsync(int fiscalYearId, List<BulkJournalEntryInput> entries);
}
