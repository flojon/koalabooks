using KoalaBooks.Domain.Interfaces;

namespace KoalaBooks.Domain;

public class LocalCurrentFiscalYearContext : ICurrentFiscalYearContext
{
    public int? SelectedFiscalYearId { get; set; }

    public void SetSelectedFiscalYear(int? fiscalYearId) => SelectedFiscalYearId = fiscalYearId;
}
