using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class MatchToEntryRequest
{
    [Required]
    public int? JournalEntryId { get; init; }
}
