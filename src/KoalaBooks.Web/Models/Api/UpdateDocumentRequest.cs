namespace KoalaBooks.Web.Models.Api;

// Both fields are optional and nullable — either may legitimately be cleared back to null
// (unclassifying a document, or removing its date), so neither carries [Required].
public class UpdateDocumentRequest
{
    public string? ClassifiedType { get; init; }
    public DateOnly? DocumentDate { get; init; }
}
