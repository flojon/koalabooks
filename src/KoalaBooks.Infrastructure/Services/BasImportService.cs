using ExcelDataReader;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using KoalaBooks.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text;

namespace KoalaBooks.Infrastructure.Services;

public record BasImportResult(int ImportedCount, int SkippedCount, List<string> Errors);

public class BasImportService
{
    private readonly AppDbContext _db;

    public BasImportService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BasImportResult> ImportDefaultAsync(int fiscalYearId)
    {
        var assembly = typeof(BasImportService).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            "KoalaBooks.Infrastructure.Resources.BAS_kontoplan_2026_v2.xlsx")
            ?? throw new InvalidOperationException(
                "Embedded BAS 2026 resource not found. Ensure the file is marked as EmbeddedResource.");
        return await ImportFromExcelAsync(stream, fiscalYearId).ConfigureAwait(false);
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
            .ToHashSetAsync().ConfigureAwait(false);

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

        // Detect file format by scanning header rows for "Huvudkonton" position.
        // 2026: "Huvudkonton" in col A (0) — main A/B, sub C/D
        // 2025: "Huvudkonton" in col D (3) — all 4-digit accounts in G(6)/H(7)
        // Old (.xls 2018): no "Huvudkonton" in A or D — main B(1)/C(2), sub E(4)/F(5)
        bool has2026Header = false, has2025Header = false;
        for (int i = 0; i < Math.Min(10, sheet.Rows.Count); i++)
        {
            var col0 = sheet.Rows[i][0]?.ToString()?.Trim();
            var col3 = sheet.Columns.Count > 3 ? sheet.Rows[i][3]?.ToString()?.Trim() : null;
            if (string.Equals(col0, "Huvudkonton", StringComparison.OrdinalIgnoreCase)) { has2026Header = true; break; }
            if (string.Equals(col3, "Huvudkonton", StringComparison.OrdinalIgnoreCase)) { has2025Header = true; break; }
        }

        int mainNumCol, mainNameCol, subNumCol, subNameCol;
        if (has2026Header)      { mainNumCol = 0; mainNameCol = 1; subNumCol = 2;  subNameCol = 3; }
        else if (has2025Header) { mainNumCol = 6; mainNameCol = 7; subNumCol = -1; subNameCol = -1; }
        else                    { mainNumCol = 1; mainNameCol = 2; subNumCol = 4;  subNameCol = 5; }

        for (int rowIndex = 1; rowIndex < sheet.Rows.Count; rowIndex++) // skip row 0 (title)
        {
            var row = sheet.Rows[rowIndex];

            if (mainNumCol < sheet.Columns.Count &&
                TryParseAccountNumber(row[mainNumCol], out var mainNumber) && !string.IsNullOrWhiteSpace(mainNumber))
            {
                var mainName = mainNameCol < sheet.Columns.Count ? row[mainNameCol]?.ToString()?.Trim() ?? string.Empty : string.Empty;
                ProcessAccount(mainNumber, mainName, fiscalYearId, existing, toAdd, errors, ref imported, ref skipped);
            }

            if (subNumCol >= 0 && subNumCol < sheet.Columns.Count &&
                TryParseAccountNumber(row[subNumCol], out var subNumber) && !string.IsNullOrWhiteSpace(subNumber))
            {
                var subName = subNameCol < sheet.Columns.Count ? row[subNameCol]?.ToString()?.Trim() ?? string.Empty : string.Empty;
                ProcessAccount(subNumber, subName, fiscalYearId, existing, toAdd, errors, ref imported, ref skipped);
            }
        }

        if (toAdd.Count > 0)
        {
            _db.Accounts.AddRange(toAdd);
            await _db.SaveChangesAsync().ConfigureAwait(false);
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

        var s = cell.ToString()?.Trim().TrimEnd('#'); // strip K2 marker (#)
        if (string.IsNullOrEmpty(s)) return false;

        // Skip known header marker rows
        if (s.StartsWith("BAS-konton", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("Underkonton", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("Huvudkonton", StringComparison.OrdinalIgnoreCase))
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
