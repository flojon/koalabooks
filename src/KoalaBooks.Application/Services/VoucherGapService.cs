using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Application.Services;

public class VoucherGapService
{
    private readonly AppDbContext _db;

    public VoucherGapService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<int>> FindGapsAsync(int fiscalYearId)
    {
        var numbers = await _db.JournalEntries
            .Where(j => j.FiscalYearId == fiscalYearId)
            .Select(j => j.EntryNumber)
            .ToListAsync().ConfigureAwait(false);

        if (numbers.Count == 0)
            return [];

        var present = numbers.ToHashSet();
        var max = numbers.Max();

        var gaps = new List<int>();
        for (var n = 1; n < max; n++)
        {
            if (!present.Contains(n))
                gaps.Add(n);
        }
        return gaps;
    }

    public async Task<List<int>> GetUnexplainedGapsAsync(int fiscalYearId)
    {
        var gaps = await FindGapsAsync(fiscalYearId).ConfigureAwait(false);
        if (gaps.Count == 0)
            return gaps;

        var explained = await _db.VoucherGapExplanations
            .Where(v => v.FiscalYearId == fiscalYearId)
            .Select(v => v.MissingEntryNumber)
            .ToHashSetAsync().ConfigureAwait(false);

        return gaps.Where(g => !explained.Contains(g)).ToList();
    }

    public async Task<string?> AddExplanationAsync(
        int fiscalYearId, int missingEntryNumber, string explanation, string explainedBy)
    {
        if (string.IsNullOrWhiteSpace(explanation))
            return "An explanation is required.";

        var gaps = await FindGapsAsync(fiscalYearId).ConfigureAwait(false);
        if (!gaps.Contains(missingEntryNumber))
            return $"Entry number {missingEntryNumber} is not a gap in the sequence.";

        var existing = await _db.VoucherGapExplanations
            .FirstOrDefaultAsync(v => v.FiscalYearId == fiscalYearId && v.MissingEntryNumber == missingEntryNumber).ConfigureAwait(false);

        if (existing is not null)
        {
            existing.Explanation = explanation;
            existing.ExplainedBy = explainedBy;
            existing.ExplainedAt = DateTime.UtcNow;
        }
        else
        {
            _db.VoucherGapExplanations.Add(new VoucherGapExplanation
            {
                FiscalYearId = fiscalYearId,
                MissingEntryNumber = missingEntryNumber,
                Explanation = explanation,
                ExplainedBy = explainedBy,
                ExplainedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync().ConfigureAwait(false);
        return null;
    }

    public async Task<List<VoucherGapExplanation>> GetExplanationsAsync(int fiscalYearId)
    {
        return await _db.VoucherGapExplanations
            .Where(v => v.FiscalYearId == fiscalYearId)
            .OrderBy(v => v.MissingEntryNumber)
            .ToListAsync().ConfigureAwait(false);
    }
}
