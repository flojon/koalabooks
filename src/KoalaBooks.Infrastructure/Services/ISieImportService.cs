using jsiSIE;

namespace KoalaBooks.Infrastructure.Services;

public interface ISieImportService
{
    SieDocument Parse(Stream stream);
    Task<SieImportPreview> GetPreviewAsync(SieDocument doc);
    Task<SieImportAllResult> ImportAllAsync(SieDocument doc, bool overwrite);
    Task<SieImportResult> ImportFiscalYearAsync(SieDocument doc, int rarId, bool overwrite);
}
