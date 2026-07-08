using Hangfire.Dashboard;

namespace KoalaBooks.Web.Services;

// Fails closed by design: the dashboard shows job data across all organisations
// (KoalaBooks is multi-tenant), so being authenticated is not enough - only the
// "Admin" role may view it. No user is granted that role yet, so /hangfire is
// inaccessible until an operator is seeded into it.
public class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.IsInRole("Admin");
    }
}
