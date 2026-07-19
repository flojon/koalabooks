namespace KoalaBooks.Web.Models.Api;

public record VatReportRowResponse(string AccountNumber, string AccountName, decimal Debit, decimal Credit);

public record VatReportSectionResponse(string Title, List<VatReportRowResponse> Rows, decimal Total);

public record VatReportResponse(VatReportSectionResponse OutputVat, VatReportSectionResponse InputVat, decimal NetPayable);
