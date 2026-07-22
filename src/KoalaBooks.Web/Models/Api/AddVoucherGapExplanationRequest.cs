using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class AddVoucherGapExplanationRequest
{
    [Required]
    public int? MissingEntryNumber { get; init; }

    [Required]
    [MinLength(1)]
    public string Explanation { get; init; } = "";

    [Required]
    [MinLength(1)]
    public string ExplainedBy { get; init; } = "";
}
