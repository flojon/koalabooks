namespace KoalaBooks.Domain.Interfaces;

public interface IVatReportCsvExporter
{
    byte[] Build(VatReportData data, string fiscalYearName, DateOnly? from, DateOnly? to);
}
