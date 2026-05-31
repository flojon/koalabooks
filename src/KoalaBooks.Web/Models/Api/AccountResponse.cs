using KoalaBooks.Domain.Enums;

namespace KoalaBooks.Web.Models.Api;

// AccountClass is serialised as a string (e.g. "Asset") via JsonStringEnumConverter in Program.cs
public record AccountResponse(
    int Id,
    string AccountNumber,
    string Name,
    AccountClass AccountClass,
    bool IsActive,
    decimal IncomingBalance,
    decimal OutgoingBalance);
