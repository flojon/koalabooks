using KoalaBooks.Application.Services;
using KoalaBooks.Domain;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests.Services;

public class CustomerServiceTests : IDisposable
{
    private readonly string _dbName;
    private readonly AppDbContext _db;
    private readonly LocalCurrentUser _currentUser;
    private readonly int _organisationId;

    public CustomerServiceTests()
    {
        var (dbName, connStr) = PostgresContainerFixture.CreateUniqueDatabase();
        _dbName = dbName;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connStr, o => o.EnableRetryOnFailure())
            .Options;

        _currentUser = new LocalCurrentUser();
        _db = new AppDbContext(options, _currentUser);
        _db.Database.EnsureCreated();

        var org = new Organisation { Name = "Test Org", Slug = "test-org-customer-svc" };
        _db.Organisations.Add(org);
        _db.SaveChanges();
        _organisationId = org.Id;
        _currentUser.OrganisationId = _organisationId;
    }

    public void Dispose()
    {
        _db.Dispose();
        PostgresContainerFixture.DropDatabase(_dbName);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsCustomer_WhenBelongsToCurrentTenant()
    {
        var customer = new Customer { OrganisationId = _organisationId, Name = "Acme AB" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var service = new CustomerService(_db);
        var found = await service.GetByIdAsync(customer.Id);

        Assert.NotNull(found);
        Assert.Equal("Acme AB", found!.Name);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenUnknownId()
    {
        var service = new CustomerService(_db);
        var found = await service.GetByIdAsync(999999);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenBelongsToDifferentTenant()
    {
        var otherOrg = new Organisation { Name = "Other Org", Slug = "other-org-customer-svc" };
        _db.Organisations.Add(otherOrg);
        await _db.SaveChangesAsync();

        var otherCustomer = new Customer { OrganisationId = otherOrg.Id, Name = "Other Customer" };
        _db.Customers.Add(otherCustomer);
        await _db.SaveChangesAsync();

        var service = new CustomerService(_db);
        var found = await service.GetByIdAsync(otherCustomer.Id);

        Assert.Null(found);
    }
}
