using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record JournalEntryResponse(
    int Id,
    int EntryNumber,
    DateOnly Date,
    string Description,
    bool IsPosted,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] JournalEntryStatus Status,
    int? SourceJournalEntryId,
    DateTime CreatedAt,
    List<JournalEntryLineResponse> Lines);
