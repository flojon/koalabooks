using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace KoalaBooks.Web.Models.Api;

public class UploadDocumentRequest
{
    [Required]
    public IFormFile? File { get; init; }
}
