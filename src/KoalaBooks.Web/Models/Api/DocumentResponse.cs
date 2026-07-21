using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record DocumentResponse(
    int Id,
    string FileName,
    string ContentType,
    long FileSize,
    DateTime UploadedAt,
    string? ClassifiedType,
    string? SuggestedType,
    string? ExtractedDataJson,
    DateOnly? DocumentDate,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ExtractionStatus ExtractionStatus);
