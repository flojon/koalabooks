using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

internal static class JournalEntryExtensions
{
    // Advisory lock key space: 43000 + fiscalYearId (distinct from invoice number locks at 42000+).
    // Prevents duplicate entry numbers under concurrent PostAsync / MarkAsPaidAsync calls.
    internal static async Task<int> NextEntryNumberAsync(this AppDbContext db, int fiscalYearId)
    {
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(43000 + {0})", fiscalYearId).ConfigureAwait(false);
        return (await db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber).ConfigureAwait(false) ?? 0) + 1;
    }
}
