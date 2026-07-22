using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using System.Text.Json.Serialization;

namespace KoalaBooks.Web.Models.Api;

public record OrganisationResponse(
    int Id,
    string Name,
    string Slug,
    string? OrgNumber,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] LegalForm LegalForm)
{
    public static OrganisationResponse From(Organisation o) =>
        new(o.Id, o.Name, o.Slug, o.OrgNumber, o.LegalForm);
}
