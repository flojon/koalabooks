using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class UpdateJournalEntryRequest
{
    [Required]
    public DateOnly Date { get; init; }

    [Required]
    public string Description { get; init; } = "";

    [MinLength(1)]
    public List<CreateJournalEntryLineRequest> Lines { get; init; } = [];
}
