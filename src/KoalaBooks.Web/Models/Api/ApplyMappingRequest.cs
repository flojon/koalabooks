using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class MappingRowRequest
{
    [Required]
    public string SourceAccountNumber { get; init; } = "";

    [Required]
    public string SourceAccountName { get; init; } = "";

    public decimal Ub { get; init; }

    public string? TargetAccountNumber { get; init; }
}

public class ApplyMappingRequest
{
    [MinLength(1)]
    public List<MappingRowRequest> Rows { get; init; } = [];
}
