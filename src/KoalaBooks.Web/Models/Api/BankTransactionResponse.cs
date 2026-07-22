using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record BankTransactionResponse(
    int Id,
    int AccountId,
    string AccountNumber,
    string AccountName,
    DateOnly Date,
    decimal Amount,
    string Description,
    string? Reference,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BankTransactionStatus Status,
    int? JournalEntryId);
