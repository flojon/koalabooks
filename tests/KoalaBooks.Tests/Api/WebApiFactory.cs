using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KoalaBooks.Tests;

public class WebApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connStr;

    public WebApiFactory(string connStr) => _connStr = connStr;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting injects the value into IConfiguration before Program.cs's builder.Build() runs,
        // so Aspire's AddNpgsqlDbContext picks it up during its options validation.
        builder.UseSetting("ConnectionStrings:koalabooks", _connStr);
    }
}
