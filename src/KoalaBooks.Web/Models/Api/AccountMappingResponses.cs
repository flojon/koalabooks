namespace KoalaBooks.Web.Models.Api;

public record MappingRowResponse(
    string SourceAccountNumber,
    string SourceAccountName,
    decimal Ub,
    string? TargetAccountNumber);

public record ApplyMappingResultResponse(int Mapped, int Skipped);
