using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace KoalaBooks.Web.Models.Api;

public class ParsePreviewRequest
{
    [Required]
    public IFormFile? File { get; init; }

    [Required]
    public int? DateCol { get; init; }

    [Required]
    public int? AmountCol { get; init; }

    [Required]
    public int? DescCol { get; init; }

    public int? RefCol { get; init; }

    [Required]
    public string? DateFormat { get; init; }
}
