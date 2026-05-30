using KoalaBooks.Domain.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace KoalaBooks.Web.Services;

public class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUser(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public int? OrganisationId
    {
        get
        {
            var value = _accessor.HttpContext?.User?.FindFirstValue("org_id");
            return int.TryParse(value, out var id) ? id : null;
        }
    }
}
