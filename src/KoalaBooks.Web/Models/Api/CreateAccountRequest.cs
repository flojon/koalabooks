using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Web.Models.Api;

public class CreateAccountRequest
{
    [Required]
    public string AccountNumber { get; init; } = "";

    [Required]
    public string Name { get; init; } = "";

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AccountClass? AccountClass { get; init; }
}
