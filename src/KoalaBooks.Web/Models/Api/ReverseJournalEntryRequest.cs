using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class ReverseJournalEntryRequest
{
    [Required]
    public string Reason { get; init; } = "";
}
