using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record BackgroundJobRunResponse(
    int Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BackgroundJobType JobType,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BackgroundJobStatus Status,
    int ProcessedCount,
    int? TotalCount,
    string? ResultJson,
    bool Acknowledged,
    DateTime CreatedAt);
