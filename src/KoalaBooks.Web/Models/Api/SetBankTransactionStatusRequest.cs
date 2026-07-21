using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Web.Models.Api;

public class SetBankTransactionStatusRequest
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BankTransactionStatus? Status { get; init; }
}
