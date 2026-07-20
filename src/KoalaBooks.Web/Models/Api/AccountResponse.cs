using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record AccountResponse(
    int Id,
    string AccountNumber,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] AccountClass AccountClass,
    bool IsActive,
    decimal IncomingBalance,
    decimal OutgoingBalance)
{
    public static AccountResponse From(Account a) =>
        new(a.Id, a.AccountNumber, a.Name, a.AccountClass, a.IsActive, a.IncomingBalance, a.OutgoingBalance);
}
