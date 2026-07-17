namespace KoalaBooks.Domain.Interfaces;

public interface ICurrentFiscalYearContext
{
    int? SelectedFiscalYearId { get; }
    void SetSelectedFiscalYear(int? fiscalYearId);
}
