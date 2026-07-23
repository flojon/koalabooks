namespace KoalaBooks.Web.Models.Api;

public record BulkJournalImportResultResponse(bool Success, string? Error, int? FailedEntryIndex, List<int> CreatedEntryIds);
