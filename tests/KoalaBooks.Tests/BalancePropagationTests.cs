using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class BalancePropagationTests : IDisposable
{
    private readonly TestFixture _f;

    public BalancePropagationTests()
    {
        _f = new TestFixture();
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task CreateFiscalYear_CopiesAccountsFromPreviousYear()
    {
        // Setup: previous year with accounts that have outgoing balances
        var fy2025 = new FiscalYear
        {
            Name = "2025",
            StartDate = new DateOnly(2025, 1, 1),
            EndDate = new DateOnly(2025, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.Add(fy2025);
        await _f.Db.SaveChangesAsync();

        _f.Db.Accounts.AddRange(
            new Account
            {
                AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset,
                FiscalYearId = fy2025.Id, IncomingBalance = 30000m, OutgoingBalance = 50000m
            },
            new Account
            {
                AccountNumber = "1930", Name = "Företagskonto", AccountClass = AccountClass.Asset,
                FiscalYearId = fy2025.Id, IncomingBalance = 100000m, OutgoingBalance = 120000m
            });
        await _f.Db.SaveChangesAsync();

        // Act: create new fiscal year
        var fy2026 = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        // Assert: accounts copied with IB = previous UB
        var newAccounts = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == fy2026.Id)
            .OrderBy(a => a.AccountNumber)
            .ToListAsync();

        Assert.Equal(2, newAccounts.Count);

        Assert.Equal("1910", newAccounts[0].AccountNumber);
        Assert.Equal("Kassa", newAccounts[0].Name);
        Assert.Equal(50000m, newAccounts[0].IncomingBalance);
        Assert.Equal(0m, newAccounts[0].OutgoingBalance);

        Assert.Equal("1930", newAccounts[1].AccountNumber);
        Assert.Equal(120000m, newAccounts[1].IncomingBalance);
    }

    [Fact]
    public async Task CreateFiscalYear_NoPreviousYear_NoAccounts()
    {
        var fy = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31)
        });

        var accounts = await _f.Db.Accounts.Where(a => a.FiscalYearId == fy.Id).ToListAsync();
        Assert.Empty(accounts);
    }

    [Fact]
    public async Task PropagateBalances_UpdatesNextYearIncomingBalances()
    {
        // Setup: two consecutive fiscal years
        var fy2025 = new FiscalYear
        {
            Name = "2025",
            StartDate = new DateOnly(2025, 1, 1),
            EndDate = new DateOnly(2025, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        var fy2026 = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.AddRange(fy2025, fy2026);
        await _f.Db.SaveChangesAsync();

        _f.Db.Accounts.AddRange(
            new Account
            {
                AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset,
                FiscalYearId = fy2025.Id, OutgoingBalance = 75000m
            },
            new Account
            {
                AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset,
                FiscalYearId = fy2026.Id, IncomingBalance = 50000m // old value
            });
        await _f.Db.SaveChangesAsync();

        // Act: propagate from 2025 to 2026
        await _f.FiscalYearService.PropagateBalancesToNextYearAsync(fy2025.Id);

        // Assert: 2026's IB updated to 2025's UB
        var account2026 = await _f.Db.Accounts.SingleAsync(a =>
            a.FiscalYearId == fy2026.Id && a.AccountNumber == "1910");
        Assert.Equal(75000m, account2026.IncomingBalance);
    }

    [Fact]
    public async Task PropagateBalances_CreatesAccountInNextYear_IfMissing()
    {
        var fy2025 = new FiscalYear
        {
            Name = "2025",
            StartDate = new DateOnly(2025, 1, 1),
            EndDate = new DateOnly(2025, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        var fy2026 = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.AddRange(fy2025, fy2026);
        await _f.Db.SaveChangesAsync();

        _f.Db.Accounts.Add(new Account
        {
            AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset,
            FiscalYearId = fy2025.Id, OutgoingBalance = 75000m
        });
        await _f.Db.SaveChangesAsync();

        // Act
        await _f.FiscalYearService.PropagateBalancesToNextYearAsync(fy2025.Id);

        // Assert: account created in 2026 with IB = 75000
        var newAccount = await _f.Db.Accounts.SingleAsync(a =>
            a.FiscalYearId == fy2026.Id && a.AccountNumber == "1910");
        Assert.Equal(75000m, newAccount.IncomingBalance);
        Assert.Equal(0m, newAccount.OutgoingBalance);
    }

    [Fact]
    public async Task PropagateBalances_NoNextYear_DoesNothing()
    {
        var fy2025 = new FiscalYear
        {
            Name = "2025",
            StartDate = new DateOnly(2025, 1, 1),
            EndDate = new DateOnly(2025, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.Add(fy2025);
        await _f.Db.SaveChangesAsync();

        _f.Db.Accounts.Add(new Account
        {
            AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset,
            FiscalYearId = fy2025.Id, OutgoingBalance = 75000m
        });
        await _f.Db.SaveChangesAsync();

        // Act: propagate with no next year — should not throw
        await _f.FiscalYearService.PropagateBalancesToNextYearAsync(fy2025.Id);

        // Still only 1 account
        Assert.Equal(1, await _f.Db.Accounts.CountAsync());
    }

    [Fact]
    public async Task PropagateBalances_SkipsZeroOutgoingBalance()
    {
        var fy2025 = new FiscalYear
        {
            Name = "2025",
            StartDate = new DateOnly(2025, 1, 1),
            EndDate = new DateOnly(2025, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        var fy2026 = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.AddRange(fy2025, fy2026);
        await _f.Db.SaveChangesAsync();

        _f.Db.Accounts.Add(new Account
        {
            AccountNumber = "5010", Name = "Lokalhyra", AccountClass = AccountClass.Expense,
            FiscalYearId = fy2025.Id, OutgoingBalance = 0m
        });
        await _f.Db.SaveChangesAsync();

        await _f.FiscalYearService.PropagateBalancesToNextYearAsync(fy2025.Id);

        // Should NOT create a new account with zero balance
        Assert.Empty(await _f.Db.Accounts.Where(a => a.FiscalYearId == fy2026.Id).ToListAsync());
    }

    [Fact]
    public async Task CreateFiscalYear_DoesNotDuplicateExistingAccounts()
    {
        // Setup: previous year
        var fy2025 = new FiscalYear
        {
            Name = "2025",
            StartDate = new DateOnly(2025, 1, 1),
            EndDate = new DateOnly(2025, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.Add(fy2025);
        await _f.Db.SaveChangesAsync();

        _f.Db.Accounts.Add(new Account
        {
            AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset,
            FiscalYearId = fy2025.Id, OutgoingBalance = 50000m
        });
        await _f.Db.SaveChangesAsync();

        // Create 2026 and pre-populate an account (e.g., from SIE import)
        var fy2026 = new FiscalYear
        {
            Name = "2026",
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 12, 31),
            OrganisationId = _f.TestOrgId
        };
        _f.Db.FiscalYears.Add(fy2026);
        await _f.Db.SaveChangesAsync();

        _f.Db.Accounts.Add(new Account
        {
            AccountNumber = "1910", Name = "Kassa", AccountClass = AccountClass.Asset,
            FiscalYearId = fy2026.Id, IncomingBalance = 99999m
        });
        await _f.Db.SaveChangesAsync();

        // Create another year — should pick 2026 as previous (newest before 2027)
        var fy2027 = await _f.FiscalYearService.CreateAsync(new FiscalYear
        {
            OrganisationId = _f.DefaultOrg.Id,
            Name = "2027",
            StartDate = new DateOnly(2027, 1, 1),
            EndDate = new DateOnly(2027, 12, 31)
        });

        // Only 1 account in 2027 (from 2026), not duplicated
        var accounts = await _f.Db.Accounts.Where(a => a.FiscalYearId == fy2027.Id).ToListAsync();
        Assert.Single(accounts);
    }
}
