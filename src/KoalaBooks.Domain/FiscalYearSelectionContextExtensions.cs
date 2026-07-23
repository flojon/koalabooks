using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Domain;

public static class FiscalYearSelectionContextExtensions
{
    public static async Task<FiscalYear?> ResolveSeedAsync(
        this FiscalYearSelectionContext context,
        IFiscalYearService fiscalYearService,
        List<FiscalYear> candidates,
        FiscalYear? extraFallback = null)
    {
        FiscalYear? seed = null;
        if (context.LastSelectedFiscalYearId is { } lastId)
            seed = candidates.FirstOrDefault(f => f.Id == lastId);
        seed ??= await fiscalYearService.GetDefaultFiscalYearAsync() ?? extraFallback ?? candidates.FirstOrDefault();
        return seed;
    }
}
