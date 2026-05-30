using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace KoalaBooks.Tests;

public class BasImportServiceTests : IDisposable
{
    private readonly TestFixture _f;
    private readonly BasImportService _service;
    private readonly FiscalYear _fy;

    public BasImportServiceTests()
    {
        _f = new TestFixture();
        _service = new BasImportService(_f.Db);
        _fy = _f.CreateFiscalYear();
    }

    public void Dispose() => _f.Dispose();

    // ── Real BAS XLS file integration tests ──────────────────────

    [Fact]
    public async Task ImportFromExcel_RealBasFile_ImportsAccounts()
    {
        var filePath = Path.Combine(FindRepoRoot(), "resources", "Kontoplan_K1_2018.xls");
        using var stream = File.OpenRead(filePath);

        var result = await _service.ImportFromExcelAsync(stream, _fy.Id);

        Assert.True(result.ImportedCount > 0, "Should import at least some accounts from BAS file");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ImportFromExcel_RealBasFile_CreatesAccountsInDatabase()
    {
        var filePath = Path.Combine(FindRepoRoot(), "resources", "Kontoplan_K1_2018.xls");
        using var stream = File.OpenRead(filePath);

        await _service.ImportFromExcelAsync(stream, _fy.Id);

        var accounts = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == _fy.Id)
            .ToListAsync();
        Assert.NotEmpty(accounts);
        Assert.All(accounts, a =>
        {
            Assert.Equal(4, a.AccountNumber.Length);
            Assert.NotEmpty(a.Name);
            Assert.Equal(_fy.Id, a.FiscalYearId);
            Assert.True(a.IsActive);
        });
    }

    [Fact]
    public async Task ImportFromExcel_RealBasFile_MapsAccountClassesCorrectly()
    {
        var filePath = Path.Combine(FindRepoRoot(), "resources", "Kontoplan_K1_2018.xls");
        using var stream = File.OpenRead(filePath);

        await _service.ImportFromExcelAsync(stream, _fy.Id);

        var accounts = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == _fy.Id)
            .ToListAsync();

        // 1xxx = Asset
        var assetAccounts = accounts.Where(a => a.AccountNumber.StartsWith("1")).ToList();
        Assert.NotEmpty(assetAccounts);
        Assert.All(assetAccounts, a => Assert.Equal(AccountClass.Asset, a.AccountClass));

        // 3xxx = Revenue
        var revenueAccounts = accounts.Where(a => a.AccountNumber.StartsWith("3")).ToList();
        if (revenueAccounts.Count > 0)
            Assert.All(revenueAccounts, a => Assert.Equal(AccountClass.Revenue, a.AccountClass));

        // 5xxx-6xxx = Expense
        var expenseAccounts = accounts
            .Where(a => a.AccountNumber.StartsWith("5") || a.AccountNumber.StartsWith("6"))
            .ToList();
        if (expenseAccounts.Count > 0)
            Assert.All(expenseAccounts, a => Assert.Equal(AccountClass.Expense, a.AccountClass));
    }

    [Fact]
    public async Task ImportFromExcel_RealBasFile_AllAccountNumbersAreFourDigits()
    {
        var filePath = Path.Combine(FindRepoRoot(), "resources", "Kontoplan_K1_2018.xls");
        using var stream = File.OpenRead(filePath);

        await _service.ImportFromExcelAsync(stream, _fy.Id);

        var accounts = await _f.Db.Accounts
            .Where(a => a.FiscalYearId == _fy.Id)
            .ToListAsync();
        Assert.All(accounts, a =>
        {
            Assert.Matches(@"^\d{4}$", a.AccountNumber);
        });
    }

    // ── Deduplication tests ──────────────────────────────────────

    [Fact]
    public async Task ImportFromExcel_ExistingAccount_IsSkipped()
    {
        // Pre-create an account that exists in the BAS file
        _f.CreateAccount(_fy.Id, "1910", "Kassa", AccountClass.Asset);

        var filePath = Path.Combine(FindRepoRoot(), "resources", "Kontoplan_K1_2018.xls");
        using var stream = File.OpenRead(filePath);

        var result = await _service.ImportFromExcelAsync(stream, _fy.Id);

        Assert.True(result.SkippedCount > 0, "Should skip at least the pre-existing account");

        // Should not create a duplicate
        var count1910 = await _f.Db.Accounts
            .CountAsync(a => a.FiscalYearId == _fy.Id && a.AccountNumber == "1910");
        Assert.Equal(1, count1910);
    }

    [Fact]
    public async Task ImportFromExcel_RunTwice_SecondRunSkipsAll()
    {
        var filePath = Path.Combine(FindRepoRoot(), "resources", "Kontoplan_K1_2018.xls");

        using (var stream1 = File.OpenRead(filePath))
            await _service.ImportFromExcelAsync(stream1, _fy.Id);

        var countAfterFirst = await _f.Db.Accounts
            .CountAsync(a => a.FiscalYearId == _fy.Id);

        using (var stream2 = File.OpenRead(filePath))
        {
            var result2 = await _service.ImportFromExcelAsync(stream2, _fy.Id);
            Assert.Equal(0, result2.ImportedCount);
            Assert.True(result2.SkippedCount > 0);
        }

        var countAfterSecond = await _f.Db.Accounts
            .CountAsync(a => a.FiscalYearId == _fy.Id);
        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    // ── Error handling tests ─────────────────────────────────────

    [Fact]
    public async Task ImportFromExcel_InvalidStream_ReturnsError()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("this is not an excel file"));

        var result = await _service.ImportFromExcelAsync(stream, _fy.Id);

        Assert.Equal(0, result.ImportedCount);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("Failed to open Excel file"));
    }

    [Fact]
    public async Task ImportFromExcel_EmptyStream_ReturnsError()
    {
        using var stream = new MemoryStream();

        var result = await _service.ImportFromExcelAsync(stream, _fy.Id);

        Assert.Equal(0, result.ImportedCount);
        Assert.NotEmpty(result.Errors);
    }

    // ── Embedded default import ──────────────────────────────────

    [Fact]
    public async Task ImportDefaultAsync_ImportsAccounts()
    {
        var result = await _service.ImportDefaultAsync(_fy.Id);

        Assert.True(result.ImportedCount > 1000,
            $"Expected >1000 accounts from BAS 2026, got {result.ImportedCount}");
        Assert.Empty(result.Errors);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                Directory.Exists(Path.Combine(dir, "resources")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not find repository root. Ensure 'resources/' directory exists.");
    }
}
