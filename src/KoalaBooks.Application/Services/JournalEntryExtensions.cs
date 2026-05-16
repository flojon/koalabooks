using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

internal static class JournalEntryExtensions
{
    internal static async Task<int> NextEntryNumberAsync(this AppDbContext db, int fiscalYearId) =>
        (await db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .MaxAsync(j => (int?)j.EntryNumber) ?? 0) + 1;
}
