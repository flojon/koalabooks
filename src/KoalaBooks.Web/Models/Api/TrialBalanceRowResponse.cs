using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record TrialBalanceRowResponse(
    string AccountNumber,
    string AccountName,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountClass AccountClass,
    decimal IncomingBalance,
    decimal TotalDebit,
    decimal TotalCredit,
    decimal Balance);
