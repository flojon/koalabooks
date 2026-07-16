namespace KoalaBooks.Application.Services;

public interface IVatReportCsvExporter
{
    byte[] Build(VatReportData data, string fiscalYearName, DateOnly? from, DateOnly? to);
}
