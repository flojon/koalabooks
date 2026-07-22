using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class BulkJournalImportService(AppDbContext db, IJournalEntryService journalEntryService) : IBulkJournalImportService
{
    public async Task<BulkJournalImportResult> ImportAsync(int fiscalYearId, List<BulkJournalEntryInput> entries)
    {
        if (entries.Count == 0)
            return new BulkJournalImportResult(false, "At least one entry is required.", null, []);

        // Wrapped in the execution strategy because EnrichNpgsqlDbContext enables a
        // retrying strategy, which refuses user-initiated transactions run outside of it
        // (see SupplierInvoiceService.PostAsync for the same pattern).
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ImportInTransactionAsync(fiscalYearId, entries)).ConfigureAwait(false);
    }

    private async Task<BulkJournalImportResult> ImportInTransactionAsync(int fiscalYearId, List<BulkJournalEntryInput> entries)
    {
#pragma warning disable CA2007 // await using's variable is used below; ConfigureAwait would strip its members
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007

        var journalEntries = entries.Select(input => new JournalEntry
        {
            FiscalYearId = fiscalYearId,
            Date = input.Date,
            Description = input.Description,
            Lines = input.Lines.Select(l => new JournalEntryLine
            {
                AccountId = l.AccountId,
                DebitAmount = l.DebitAmount,
                CreditAmount = l.CreditAmount
            }).ToList()
        }).ToList();

        // CreateManyAsync validates every entry before adding any of them to the context, so
        // a validation failure leaves nothing to roll back here — the rollback only matters
        // if SaveChangesAsync itself fails after validation passes.
        var (created, error, failedIndex) = await journalEntryService.CreateManyAsync(fiscalYearId, journalEntries).ConfigureAwait(false);
        if (error is not null)
        {
            await tx.RollbackAsync().ConfigureAwait(false);
            return new BulkJournalImportResult(false, error, failedIndex, []);
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return new BulkJournalImportResult(true, null, null, created.Select(e => e.Id).ToList());
    }
}
