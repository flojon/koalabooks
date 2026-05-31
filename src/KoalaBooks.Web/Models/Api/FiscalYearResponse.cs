namespace KoalaBooks.Web.Models.Api;

public record FiscalYearResponse(int Id, string Name, DateOnly StartDate, DateOnly EndDate, bool IsClosed);
