using ExcelDataReader;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text;

namespace KoalaBooks.Application.Services;

public record BasImportResult(int ImportedCount, int SkippedCount, List<string> Errors);

public class BasImportService
{
    private readonly AppDbContext _db;

    public BasImportService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BasImportResult> ImportFromExcelAsync(Stream fileStream, int fiscalYearId)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        int imported = 0;
        int skipped = 0;
        var errors = new List<string>();

        // Load existing account numbers for this fiscal year to avoid duplicates
        var existing = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .Select(a => a.AccountNumber)
            .ToHashSetAsync();

        DataTable sheet;
        try
        {
            using var reader = ExcelReaderFactory.CreateReader(fileStream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false }
            });
            sheet = dataSet.Tables[0];
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to open Excel file: {ex.Message}");
            return new BasImportResult(0, 0, errors);
        }

        var toAdd = new List<Account>();

        for (int rowIndex = 1; rowIndex < sheet.Rows.Count; rowIndex++) // skip row 0 (title)
        {
            var row = sheet.Rows[rowIndex];

            // Process main account (cols B=1, C=2, D=3)
            if (TryParseAccountNumber(row[1], out var mainNumber) && !string.IsNullOrWhiteSpace(mainNumber))
            {
                var mainName = row[2]?.ToString()?.Trim() ?? string.Empty;
                ProcessAccount(mainNumber, mainName, fiscalYearId, existing, toAdd, errors, ref imported, ref skipped);
            }

            // Process sub-account (cols E=4, F=5, G=6)
            if (TryParseAccountNumber(row[4], out var subNumber) && !string.IsNullOrWhiteSpace(subNumber))
            {
                var subName = row[5]?.ToString()?.Trim() ?? string.Empty;
                ProcessAccount(subNumber, subName, fiscalYearId, existing, toAdd, errors, ref imported, ref skipped);
            }
        }

        if (toAdd.Count > 0)
        {
            _db.Accounts.AddRange(toAdd);
            await _db.SaveChangesAsync();
        }

        return new BasImportResult(imported, skipped, errors);
    }

    private static void ProcessAccount(
        string accountNumber,
        string name,
        int fiscalYearId,
        HashSet<string> existing,
        List<Account> toAdd,
        List<string> errors,
        ref int imported,
        ref int skipped)
    {
        if (existing.Contains(accountNumber))
        {
            skipped++;
            return;
        }

        var accountClass = AccountClassMapper.FromAccountNumber(accountNumber);
        if (accountClass == null)
        {
            errors.Add($"Could not determine account class for account {accountNumber}");
            skipped++;
            return;
        }

        var account = new Account
        {
            AccountNumber = accountNumber,
            Name = string.IsNullOrWhiteSpace(name) ? accountNumber : name,
            AccountClass = accountClass.Value,
            FiscalYearId = fiscalYearId,
            IsActive = true
        };

        toAdd.Add(account);
        existing.Add(accountNumber); // prevent duplicates within same import
        imported++;
    }

    /// <summary>
    /// Tries to parse a cell value as a BAS account number (4-digit string).
    /// Returns false for empty cells, header strings like "BAS-konton"/"Underkonton", and non-numeric text.
    /// </summary>
    private static bool TryParseAccountNumber(object? cell, out string accountNumber)
    {
        accountNumber = string.Empty;
        if (cell == null || cell is DBNull) return false;

        // ExcelDataReader returns doubles for numeric cells in .xls
        if (cell is double d)
        {
            var n = (int)d;
            if (n < 1000 || n > 9999) return false; // only 4-digit BAS accounts
            accountNumber = n.ToString();
            return true;
        }

        var s = cell.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return false;

        // Skip known header marker rows
        if (s.StartsWith("BAS-konton", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("Underkonton", StringComparison.OrdinalIgnoreCase))
            return false;

        // Reject group headers (text that is not purely numeric, e.g. "10 Immateriella...")
        if (!double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;

        var num = (int)parsed;
        if (num < 1000 || num > 9999) return false;
        accountNumber = num.ToString();
        return true;
    }
}
