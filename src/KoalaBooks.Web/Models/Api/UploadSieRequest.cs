using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace KoalaBooks.Web.Models.Api;

public class UploadSieRequest
{
    [Required]
    public IFormFile? File { get; init; }
}

public class ImportSieRequest
{
    [Required]
    public IFormFile? File { get; init; }

    public bool Overwrite { get; init; }

    /// <summary>When null, imports every fiscal year in the file (ImportAllAsync); when
    /// set, imports only that RAR id's fiscal year (ImportFiscalYearAsync).</summary>
    public int? RarId { get; init; }
}
