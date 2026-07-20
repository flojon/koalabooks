using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class CopyAccountsRequest
{
    [Required]
    [MinLength(1)]
    public List<int> AccountIds { get; init; } = [];
}
