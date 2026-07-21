using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class SuggestContraAccountRequest
{
    [Required]
    public string Description { get; init; } = "";

    [Required]
    public decimal? Amount { get; init; }
}
