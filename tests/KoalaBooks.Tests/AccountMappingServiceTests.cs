using KoalaBooks.Application.Services;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Tests;

public class AccountMappingServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly AccountMappingService _service;

    public AccountMappingServiceTests()
    {
        _f = new TestFixture();
        _service = new AccountMappingService(_f.Db);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task BuildMapping_PreSelectsSameAccountNumber()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
            outgoingBalance: 500);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset);

        var rows = await _service.BuildMappingAsync(source.Id, target.Id);

        var row = Assert.Single(rows);
        Assert.Equal("1910", row.SourceAccountNumber);
        Assert.Equal(500, row.Ub);
        Assert.Equal("1910", row.TargetAccountNumber);
    }

    [Fact]
    public async Task BuildMapping_LeavesBlank_WhenTargetMissing()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        _f.CreateAccount(source.Id, "1241", "Personbilar", AccountClass.Asset,
            outgoingBalance: 200);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        // 1241 does not exist in target

        var rows = await _service.BuildMappingAsync(source.Id, target.Id);

        var row = Assert.Single(rows);
        Assert.Equal("1241", row.SourceAccountNumber);
        Assert.Null(row.TargetAccountNumber);
    }

    [Fact]
    public async Task ApplyMapping_WritesIbToTargetAccounts()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var cash = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset);
        var liab = _f.CreateAccount(target.Id, "2440", "Leverantörsskulder", AccountClass.Liability);

        var rows = new List<MappingRow>
        {
            new("1910", "Kassa", 500m, "1910"),
            new("2440", "Leverantörsskulder", 300m, "2440")
        };

        await _service.ApplyMappingAsync(source.Id, target.Id, rows);

        await _f.Db.Entry(cash).ReloadAsync();
        await _f.Db.Entry(liab).ReloadAsync();
        Assert.Equal(500m, cash.IncomingBalance);
        Assert.Equal(300m, liab.IncomingBalance);
    }

    [Fact]
    public async Task ApplyMapping_SetsPreviousFiscalYearId()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset);

        var rows = new List<MappingRow> { new("1910", "Kassa", 100m, "1910") };
        await _service.ApplyMappingAsync(source.Id, target.Id, rows);

        await _f.Db.Entry(target).ReloadAsync();
        Assert.Equal(source.Id, target.PreviousFiscalYearId);
    }

    [Fact]
    public async Task ApplyMapping_SkipsNullTargetRows()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), isClosed: true);
        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var cash = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 0);

        var rows = new List<MappingRow>
        {
            new("1910", "Kassa", 500m, null),
            new("1241", "Personbilar", 200m, null)
        };

        var result = await _service.ApplyMappingAsync(source.Id, target.Id, rows);

        Assert.Equal(0, result.Mapped);
        Assert.Equal(2, result.Skipped);
        await _f.Db.Entry(cash).ReloadAsync();
        Assert.Equal(0, cash.IncomingBalance);
    }

    [Fact]
    public async Task PostEntry_PropagatesAffectedAccountsToLinkedNextYear()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var cash2025 = _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 100);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        target.PreviousFiscalYearId = source.Id;
        var cash2026 = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 100);
        _f.Db.SaveChanges();

        var liab2025 = _f.CreateAccount(source.Id, "2440", "Lev.skulder", AccountClass.Liability);
        var entry = _f.MakeEntry(source.Id, cash2025.Id, liab2025.Id, 50m,
            new DateOnly(2025, 6, 1));
        _f.Db.JournalEntries.Add(entry);
        _f.Db.SaveChanges();

        var error = await _f.JournalEntryService.PostAsync(entry.Id);

        Assert.Null(error);
        // UB for 1910 in 2025 = IB(100) + debit(50) = 150 (asset: debit-normal)
        await _f.Db.Entry(cash2026).ReloadAsync();
        Assert.Equal(150m, cash2026.IncomingBalance);
    }

    [Fact]
    public async Task PostEntry_DoesNotPropagateWhenNoLinkedYear()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var cash = _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 100);
        var liab = _f.CreateAccount(source.Id, "2440", "Lev.skulder", AccountClass.Liability);

        var entry = _f.MakeEntry(source.Id, cash.Id, liab.Id, 50m, new DateOnly(2025, 6, 1));
        _f.Db.JournalEntries.Add(entry);
        _f.Db.SaveChanges();

        var error = await _f.JournalEntryService.PostAsync(entry.Id);

        Assert.Null(error);
    }

    [Fact]
    public async Task SieImport_PropagatesBalancesToLinkedNextYear()
    {
        var source = _f.CreateFiscalYear("2025",
            new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31));
        var cash2025 = _f.CreateAccount(source.Id, "1910", "Kassa", AccountClass.Asset,
            outgoingBalance: 0);

        var target = _f.CreateFiscalYear("2026",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        target.PreviousFiscalYearId = source.Id;
        var cash2026 = _f.CreateAccount(target.Id, "1910", "Kassa", AccountClass.Asset,
            incomingBalance: 0);
        _f.Db.SaveChanges();

        cash2025.OutgoingBalance = 750m;
        _f.Db.SaveChanges();

        await _f.FiscalYearService.PropagateBalancesToNextYearAsync(source.Id);

        await _f.Db.Entry(cash2026).ReloadAsync();
        Assert.Equal(750m, cash2026.IncomingBalance);
    }
}
