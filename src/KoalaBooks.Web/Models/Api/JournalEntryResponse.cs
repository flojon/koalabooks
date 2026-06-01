namespace KoalaBooks.Web.Models.Api;

public record JournalEntryResponse(
    int Id,
    int EntryNumber,
    DateOnly Date,
    string Description,
    bool IsPosted,
    DateTime CreatedAt,
    List<JournalEntryLineResponse> Lines);
