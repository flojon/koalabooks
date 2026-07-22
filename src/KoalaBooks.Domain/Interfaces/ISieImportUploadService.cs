namespace KoalaBooks.Domain.Interfaces;

// Orchestrates the REST-facing async SIE import flow: stages the uploaded file, creates a
// BackgroundJobRun row, and enqueues a SieImportJob (see 5.H-1 in the #122 program plan).
// Distinct from ISieImportService (Infrastructure), which does the actual parse/import work
// once the job picks the staged file back up.
public interface ISieImportUploadService
{
    Task<(SieImportPreview? Preview, string? Error)> PreviewAsync(Stream sieFileStream);
    Task<(int? RunId, string? Error)> EnqueueImportAsync(string fileName, Func<Stream> openSieFileData, bool overwrite, int? rarId);
}
