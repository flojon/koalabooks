using KoalaBooks.Domain;
using KoalaBooks.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KoalaBooks.Infrastructure.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // dotnet ef tooling always runs as the privileged/migrator role - it needs DDL
        // rights the runtime app_user role intentionally doesn't have.
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=KoalaBooks;Username=postgres;Password=postgres");
        return new AppDbContext(optionsBuilder.Options, new LocalCurrentUser());
    }
}
