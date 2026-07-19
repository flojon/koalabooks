using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Services;

namespace KoalaBooks.Tests;

public class BankImportServiceOrganisationScopeTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly BankImportService _svc;

    public BankImportServiceOrganisationScopeTests()
    {
        _f = new TestFixture();
        _svc = new BankImportService(_f.Db, TestFixture.MakeTenant(_f.OrganisationId));
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task GetUnmatchedForOrganisationAsync_SpansMultipleOpenFiscalYears()
    {
        var fy2025 = _f.CreateFiscalYear("2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var fy2026 = _f.CreateFiscalYear("2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var acc2025 = _f.CreateAccount(fy2025.Id, "1930", "Bank");
        var acc2026 = _f.CreateAccount(fy2026.Id, "1930", "Bank");

        _f.Db.BankTransactions.AddRange(
            new BankTransaction { OrganisationId = _f.OrganisationId, AccountId = acc2025.Id, Date = new DateOnly(2025, 6, 1), Amount = 100, Description = "tx1", Status = BankTransactionStatus.Unmatched },
            new BankTransaction { OrganisationId = _f.OrganisationId, AccountId = acc2026.Id, Date = new DateOnly(2026, 6, 1), Amount = 200, Description = "tx2", Status = BankTransactionStatus.Unmatched },
            new BankTransaction { OrganisationId = _f.OrganisationId, AccountId = acc2026.Id, Date = new DateOnly(2026, 7, 1), Amount = 300, Description = "tx3", Status = BankTransactionStatus.Matched });
        await _f.Db.SaveChangesAsync();

        var unmatched = await _svc.GetUnmatchedForOrganisationAsync(_f.OrganisationId);
        var count = await _svc.CountUnmatchedForOrganisationAsync(_f.OrganisationId);

        Assert.Equal(2, unmatched.Count);
        Assert.Equal(2, count);
    }
}
