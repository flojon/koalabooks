using KoalaBooks.Domain.Entities;

namespace KoalaBooks.Tests;

public class BankTransactionQueryTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly FiscalYear _fy;
    private readonly Account _cash;

    public BankTransactionQueryTests()
    {
        _f = new TestFixture();
        _fy = _f.CreateFiscalYear();
        (_cash, _, _, _, _) = _f.CreateStandardAccounts(_fy.Id);
    }

    public void Dispose() => _f.Dispose();

    private BankTransaction AddTransaction(DateOnly date, decimal amount, string description = "Test tx")
    {
        var tx = new BankTransaction
        {
            OrganisationId = _f.OrganisationId,
            AccountId = _cash.Id,
            Date = date,
            Amount = amount,
            Description = description
        };
        _f.Db.BankTransactions.Add(tx);
        _f.Db.SaveChanges();
        return tx;
    }

    [Fact]
    public async Task GetByFiscalYearAsync_ReturnsAllTransactionsForYear()
    {
        AddTransaction(new DateOnly(2026, 2, 1), 100m);
        AddTransaction(new DateOnly(2026, 3, 1), -50m);

        var result = await _f.BankImportService.GetByFiscalYearAsync(_fy.Id, null, null, null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetByFiscalYearAsync_FiltersByDateRange()
    {
        AddTransaction(new DateOnly(2026, 1, 15), 100m);
        AddTransaction(new DateOnly(2026, 6, 15), 200m);

        var result = await _f.BankImportService.GetByFiscalYearAsync(
            _fy.Id, new DateOnly(2026, 5, 1), new DateOnly(2026, 12, 31), null);

        Assert.Single(result);
        Assert.Equal(200m, result[0].Amount);
    }

    [Fact]
    public async Task GetByFiscalYearAsync_FiltersByAccountId()
    {
        // _cash ("1910") already exists from the constructor's CreateStandardAccounts call —
        // use a distinct account number here, not another CreateStandardAccounts call, which
        // would violate the unique (FiscalYearId, AccountNumber) index.
        var otherAccount = _f.CreateAccount(_fy.Id, "1930", "Sparkonto");
        AddTransaction(new DateOnly(2026, 2, 1), 100m);
        var other = new BankTransaction
        {
            OrganisationId = _f.OrganisationId, AccountId = otherAccount.Id,
            Date = new DateOnly(2026, 2, 2), Amount = 50m, Description = "Other account"
        };
        _f.Db.BankTransactions.Add(other);
        _f.Db.SaveChanges();

        var result = await _f.BankImportService.GetByFiscalYearAsync(_fy.Id, null, null, _cash.Id);

        Assert.Single(result);
        Assert.Equal(_cash.Id, result[0].AccountId);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingTransaction_ReturnsIt()
    {
        var tx = AddTransaction(new DateOnly(2026, 2, 1), 100m, "Findable");

        var found = await _f.BankImportService.GetByIdAsync(tx.Id);

        Assert.NotNull(found);
        Assert.Equal("Findable", found.Description);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull()
    {
        var found = await _f.BankImportService.GetByIdAsync(999999);
        Assert.Null(found);
    }
}
