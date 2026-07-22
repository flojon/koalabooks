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
        using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

        var createdIds = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var input = entries[i];
            var entry = new JournalEntry
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
            };

            var (created, error) = await journalEntryService.CreateAsync(entry).ConfigureAwait(false);
            if (error is not null)
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                return new BulkJournalImportResult(false, error, i, []);
            }

            createdIds.Add(created!.Id);
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return new BulkJournalImportResult(true, null, null, createdIds);
    }
}
