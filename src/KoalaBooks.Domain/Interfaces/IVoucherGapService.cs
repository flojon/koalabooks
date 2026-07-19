using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Domain.Interfaces;

public interface IVoucherGapService
{
    Task<List<int>> FindGapsAsync(int fiscalYearId);
    Task<List<int>> GetUnexplainedGapsAsync(int fiscalYearId);
    Task<string?> AddExplanationAsync(int fiscalYearId, int missingEntryNumber, string explanation, string explainedBy);
    Task<List<VoucherGapExplanation>> GetExplanationsAsync(int fiscalYearId);
}
