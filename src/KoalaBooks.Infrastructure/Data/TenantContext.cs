using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace KoalaBooks.Infrastructure.Data;

public class TenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public int? OrganisationId
    {
        get
        {
            var value = _httpContextAccessor.HttpContext?.User?.FindFirstValue("org_id");
            return int.TryParse(value, out var id) ? id : null;
        }
    }
}
