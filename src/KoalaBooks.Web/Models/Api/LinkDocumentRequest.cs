using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Web.Models.Api;

public class LinkDocumentRequest
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DocumentEntityType? EntityType { get; init; }

    [Required]
    public int? EntityId { get; init; }
}
