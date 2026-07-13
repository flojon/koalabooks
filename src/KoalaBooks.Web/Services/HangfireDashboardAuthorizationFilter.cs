using Hangfire.Dashboard;

namespace KoalaBooks.Web.Services;

// Fails closed on purpose: job data spans all organisations (multi-tenant),
// so plain authentication isn't enough - only the "Admin" role may view it.
public class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.IsInRole("Admin");
    }
}
