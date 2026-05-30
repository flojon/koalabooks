using ExcelDataReader;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Domain.Interfaces;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Globalization;
using System.Text;

namespace KoalaBooks.Infrastructure.Services;

public record BankFileParseResult(
    bool Success,
    string? Error,
    List<string> Headers,
    List<string[]> DataRows);

public record BankTransactionPreview(
    int RowIndex,
    DateOnly? Date,
    decimal? Amount,
    string Description,
    string? Reference,
    bool IsDuplicate,
    string? ParseError);

public record BankImportResult(int Imported, int Skipped, int Duplicates, List<string> Errors);

public class BankImportService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser;

    public BankImportService(AppDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public BankFileParseResult ParseFile(Stream stream, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext is ".csv" or ".txt" or ".tsv")
            return ParseCsv(stream);

        if (ext is ".xls" or ".xlsx")
            return ParseExcel(stream);

        return new BankFileParseResult(false, $"Filformatet '{ext}' stöds ej. Använd CSV, TXT eller Excel.", [], []);
    }

    private static BankFileParseResult ParseCsv(Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        string text;
        try
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            // Detect encoding: try UTF-8 BOM first, then try UTF-8, fall back to Latin-1
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            else
                text = TryDecodeAsUtf8(bytes) ?? Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
        }
        catch (Exception ex)
        {
            return new BankFileParseResult(false, $"Kunde inte läsa filen: {ex.Message}", [], []);
        }

        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();

        if (lines.Count == 0)
            return new BankFileParseResult(false, "Filen är tom.", [], []);

        var delimiter = DetectDelimiter(lines[0]);
        var allRows = lines.Select(l => SplitCsvLine(l, delimiter)).ToList();

        if (IsSkattekonto(allRows))
            return ParseSkattekonto(allRows);

        var headers = allRows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Kolumn {i + 1}" : h.Trim()).ToList();
        var dataRows = allRows.Skip(1).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();

        if (dataRows.Count == 0)
            return new BankFileParseResult(false, "Filen innehåller inga datarader (endast rubrik).", headers, []);

        return new BankFileParseResult(true, null, headers, dataRows);
    }

    private static BankFileParseResult ParseExcel(Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DataTable sheet;
        try
        {
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false }
            });
            sheet = dataSet.Tables[0];
        }
        catch (Exception ex)
        {
            return new BankFileParseResult(false, $"Kunde inte öppna Excel-filen: {ex.Message}", [], []);
        }

        if (sheet.Rows.Count == 0)
            return new BankFileParseResult(false, "Excel-filen är tom.", [], []);

        var headers = new List<string>();
        for (int c = 0; c < sheet.Columns.Count; c++)
        {
            var h = sheet.Rows[0][c]?.ToString()?.Trim();
            headers.Add(string.IsNullOrWhiteSpace(h) ? $"Kolumn {c + 1}" : h);
        }

        var dataRows = new List<string[]>();
        for (int r = 1; r < sheet.Rows.Count; r++)
        {
            var row = new string[sheet.Columns.Count];
            bool anyValue = false;
            for (int c = 0; c < sheet.Columns.Count; c++)
            {
                var val = sheet.Rows[r][c];
                row[c] = val is DBNull || val is null ? "" : val.ToString()!.Trim();
                if (!string.IsNullOrWhiteSpace(row[c])) anyValue = true;
            }
            if (anyValue) dataRows.Add(row);
        }

        if (dataRows.Count == 0)
            return new BankFileParseResult(false, "Excel-filen innehåller inga datarader (endast rubrik).", headers, []);

        return new BankFileParseResult(true, null, headers, dataRows);
    }

    public async Task<List<BankTransactionPreview>> BuildPreviewAsync(
        int accountId,
        List<string[]> rows,
        int dateCol,
        int amountCol,
        int descCol,
        int? refCol,
        string dateFormat)
    {
        // Load existing dedup keys for this account
        var existingKeys = await _db.BankTransactions
            .Where(b => b.AccountId == accountId)
            .Select(b => new { b.Date, b.Amount, b.Description })
            .ToListAsync();

        var dupSet = existingKeys
            .Select(e => MakeKey(e.Date, e.Amount, e.Description))
            .ToHashSet();

        var previews = new List<BankTransactionPreview>();

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var errors = new List<string>();

            DateOnly? date = null;
            decimal? amount = null;
            string description = "";
            string? reference = null;

            // Parse date
            var dateStr = SafeGet(row, dateCol);
            if (string.IsNullOrWhiteSpace(dateStr))
                errors.Add("Datum saknas");
            else if (!TryParseDate(dateStr, dateFormat, out var parsedDate))
                errors.Add($"Ogiltigt datum: '{dateStr}'");
            else
                date = parsedDate;

            // Parse amount
            var amountStr = SafeGet(row, amountCol);
            if (string.IsNullOrWhiteSpace(amountStr))
                errors.Add("Belopp saknas");
            else if (!TryParseAmount(amountStr, out var parsedAmount))
                errors.Add($"Ogiltigt belopp: '{amountStr}'");
            else
                amount = parsedAmount;

            // Description
            description = SafeGet(row, descCol).Trim();
            if (string.IsNullOrWhiteSpace(description))
                errors.Add("Beskrivning saknas");

            // Reference (optional)
            if (refCol.HasValue)
                reference = SafeGet(row, refCol.Value).Trim().NullIfEmpty();

            var parseError = errors.Count > 0 ? string.Join("; ", errors) : null;

            bool isDuplicate = false;
            if (parseError is null && date.HasValue && amount.HasValue)
                isDuplicate = dupSet.Contains(MakeKey(date.Value, amount.Value, description));

            previews.Add(new BankTransactionPreview(i, date, amount, description, reference, isDuplicate, parseError));
        }

        return previews;
    }

    public async Task<BankImportResult> ImportAsync(int accountId, List<BankTransactionPreview> previews)
    {
        int imported = 0, skipped = 0, duplicates = 0;
        var errors = new List<string>();

        // Re-check duplicates at import time to handle concurrent imports
        var existingKeys = await _db.BankTransactions
            .Where(b => b.AccountId == accountId)
            .Select(b => new { b.Date, b.Amount, b.Description })
            .ToListAsync();

        var dupSet = existingKeys
            .Select(e => MakeKey(e.Date, e.Amount, e.Description))
            .ToHashSet();

        var toAdd = new List<BankTransaction>();

        foreach (var p in previews)
        {
            if (p.ParseError is not null) { skipped++; continue; }
            if (p.IsDuplicate || dupSet.Contains(MakeKey(p.Date!.Value, p.Amount!.Value, p.Description)))
            {
                duplicates++;
                continue;
            }

            var tx = new BankTransaction
            {
                OrganisationId = _currentUser.OrganisationId ?? throw new InvalidOperationException("No active tenant."),
                AccountId = accountId,
                Date = p.Date!.Value,
                Amount = p.Amount!.Value,
                Description = p.Description,
                Reference = p.Reference,
                ImportedAt = DateTime.UtcNow
            };

            toAdd.Add(tx);
            imported++;
        }

        if (toAdd.Count > 0)
        {
            _db.BankTransactions.AddRange(toAdd);
            await _db.SaveChangesAsync();
        }

        return new BankImportResult(imported, skipped, duplicates, errors);
    }

    public Task<int> CountUnmatchedAsync(int fiscalYearId) =>
        _db.BankTransactions.CountAsync(b =>
            b.Account.FiscalYearId == fiscalYearId &&
            b.Status == BankTransactionStatus.Unmatched);

    public async Task<List<BankTransaction>> GetUnmatchedAsync(int fiscalYearId)
    {
        return await _db.BankTransactions
            .Include(b => b.Account)
            .Where(b => b.Account.FiscalYearId == fiscalYearId && b.Status == BankTransactionStatus.Unmatched)
            .OrderBy(b => b.Date)
            .ThenBy(b => b.Id)
            .ToListAsync();
    }

    public async Task<List<BankTransaction>> GetByAccountAsync(int accountId)
    {
        return await _db.BankTransactions
            .Include(b => b.JournalEntry)
            .Where(b => b.AccountId == accountId)
            .OrderByDescending(b => b.Date)
            .ThenByDescending(b => b.Id)
            .ToListAsync();
    }

    public async Task<List<Account>> GetImportableAccountsAsync(int fiscalYearId, string prefix)
    {
        return await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId && a.AccountNumber.StartsWith(prefix))
            .OrderBy(a => a.AccountNumber)
            .ToListAsync();
    }

    public async Task SetStatusAsync(int bankTransactionId, BankTransactionStatus status)
    {
        var tx = await _db.BankTransactions.FirstOrDefaultAsync(b => b.Id == bankTransactionId);
        if (tx is null) return;
        tx.Status = status;
        if (status != BankTransactionStatus.Matched)
            tx.JournalEntryId = null;
        await _db.SaveChangesAsync();
    }

    public async Task<string?> MatchToEntryAsync(int bankTransactionId, int journalEntryId)
    {
        var tx = await _db.BankTransactions.FirstOrDefaultAsync(b => b.Id == bankTransactionId);
        if (tx is null) return "Banktransaktion hittades inte.";

        var entry = await _db.JournalEntries.FirstOrDefaultAsync(j => j.Id == journalEntryId);
        if (entry is null) return "Verifikation hittades inte.";

        tx.JournalEntryId = journalEntryId;
        tx.Status = BankTransactionStatus.Matched;
        await _db.SaveChangesAsync();
        return null;
    }

    public async Task<List<JournalEntry>> GetUnmatchedJournalEntriesAsync(int fiscalYearId, int bankAccountId)
    {
        // Journal entries that are posted and not yet matched to any bank transaction for this account
        var matchedEntryIds = await _db.BankTransactions
            .Where(b => b.AccountId == bankAccountId && b.JournalEntryId.HasValue)
            .Select(b => b.JournalEntryId!.Value)
            .ToHashSetAsync();

        return await _db.JournalEntries
            .Include(j => j.Lines)
            .Where(j => j.FiscalYearId == fiscalYearId && j.IsPosted && !j.IsClosingEntry)
            .Where(j => j.Lines.Any(l => l.AccountId == bankAccountId))
            .Where(j => !matchedEntryIds.Contains(j.Id))
            .OrderByDescending(j => j.Date)
            .ThenByDescending(j => j.EntryNumber)
            .ToListAsync();
    }

    public async Task<int?> SuggestContraAccountAsync(int bankAccountId, string description, decimal amount)
    {
        var desc = description.Trim().ToUpperInvariant();
        var words = desc.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var prefix = string.Join(" ", words.Take(3));

        var matched = await _db.BankTransactions
            .Where(b => b.AccountId == bankAccountId
                     && b.Status == BankTransactionStatus.Matched
                     && b.JournalEntryId.HasValue)
            .Select(b => new { DescUpper = b.Description.ToUpper(), b.JournalEntryId })
            .ToListAsync();

        var entryIds = matched
            .Where(t => t.DescUpper == desc
                     || t.DescUpper.StartsWith(prefix)
                     || desc.StartsWith(t.DescUpper.Split(' ').Take(3).Aggregate((a, b) => a + " " + b)))
            .Select(t => t.JournalEntryId!.Value)
            .ToHashSet();

        if (entryIds.Count > 0)
        {
            var fromHistory = await _db.JournalEntryLines
                .Where(l => entryIds.Contains(l.JournalEntryId) && l.AccountId != bankAccountId)
                .GroupBy(l => l.AccountId)
                .OrderByDescending(g => g.Count())
                .Select(g => (int?)g.Key)
                .FirstOrDefaultAsync();

            if (fromHistory.HasValue)
                return fromHistory;
        }

        return await GetLegalFormDefaultAsync(bankAccountId, amount);
    }

    private async Task<int?> GetLegalFormDefaultAsync(int bankAccountId, decimal amount)
    {
        var org = await _db.Organisations.FindAsync(_currentUser.OrganisationId);
        if (org is null) return null;

        var accountNumber = org.LegalForm switch
        {
            LegalForm.EnskildFirma => amount >= 0 ? "2013" : "2018",
            LegalForm.Aktiebolag => "2893",
            _ => null
        };

        if (accountNumber is null) return null;

        var bankAccount = await _db.Accounts.FindAsync(bankAccountId);
        if (bankAccount is null) return null;

        return await _db.Accounts
            .Where(a => a.FiscalYearId == bankAccount.FiscalYearId && a.AccountNumber == accountNumber)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string MakeKey(DateOnly date, decimal amount, string description)
        => $"{date:yyyy-MM-dd}|{amount:F2}|{description.Trim().ToUpperInvariant()}";

    private static string SafeGet(string[] row, int col)
        => col >= 0 && col < row.Length ? row[col] : "";

    private static bool TryParseDate(string s, string format, out DateOnly result)
    {
        s = s.Trim();

        // Try the user-selected format first
        if (DateOnly.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;

        // Fallback: try common Swedish/ISO formats
        string[] fallbacks = ["yyyy-MM-dd", "yyyyMMdd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy", "d/M/yyyy", "d-M-yyyy"];
        foreach (var fmt in fallbacks)
        {
            if (DateOnly.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
                return true;
        }

        return false;
    }

    private static bool TryParseAmount(string s, out decimal result)
    {
        s = s.Trim();

        // Remove currency symbols and common noise
        s = s.Replace("kr", "").Replace("SEK", "").Replace(" ", "").Trim();

        // Swedish format: comma decimal, period/space thousands → already stripped spaces above
        // Try replacing comma with period for Swedish decimals
        var swedish = s.Replace(".", "").Replace(",", ".");
        if (decimal.TryParse(swedish, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
            return true;

        // Try invariant (period decimal)
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
            return true;

        // Try Swedish culture directly
        if (decimal.TryParse(s, NumberStyles.Any, new CultureInfo("sv-SE"), out result))
            return true;

        result = 0;
        return false;
    }

    private static bool IsSkattekonto(List<string[]> rows)
        => rows.Take(5).Any(r => r.Length > 1 &&
            (r[1].Contains("Ingående saldo") || r[1].Contains("Utgående saldo")));

    private static BankFileParseResult ParseSkattekonto(List<string[]> rows)
    {
        // Rows with a date in col 0 are real transactions; skip company header and balance summary rows.
        var txRows = rows
            .Where(r => r.Length >= 3 &&
                        DateOnly.TryParseExact(r[0].Trim(), "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Select(r => new[] { r[0].Trim(), r[1].Trim(), r[2].Trim() })
            .ToList();

        if (txRows.Count == 0)
            return new BankFileParseResult(false, "Inga transaktionsrader hittades i Skattekontofilen.", [], []);

        return new BankFileParseResult(true, null, ["Datum", "Beskrivning", "Belopp"], txRows);
    }

    private static char DetectDelimiter(string firstLine)
    {
        var counts = new[] { ';', ',', '\t', '|' }
            .Select(d => (delimiter: d, count: firstLine.Count(c => c == d)))
            .OrderByDescending(x => x.count)
            .First();
        return counts.count > 0 ? counts.delimiter : ';';
    }

    private static string[] SplitCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return [.. fields];
    }

    private static string? TryDecodeAsUtf8(byte[] bytes)
    {
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return utf8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
