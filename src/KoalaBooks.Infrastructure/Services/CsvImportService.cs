using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Services;

public record CsvImportResult(int Created, int Updated, int Skipped, List<string> Errors);

public class CsvImportService
{
    private readonly AppDbContext _db;

    public CsvImportService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CsvImportResult> ImportAccountsAsync(Stream csvStream)
    {
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Delimiter = ",",
            TrimOptions = TrimOptions.Trim
        });

        var records = csv.GetRecords<AccountCsvRow>().ToList();

        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();
        var existingAccounts = await _db.Accounts.ToDictionaryAsync(a => a.AccountNumber);

        foreach (var row in records)
        {
            if (string.IsNullOrWhiteSpace(row.AccountNumber) || string.IsNullOrWhiteSpace(row.Name))
            {
                skipped++;
                errors.Add($"Skipped row: empty AccountNumber or Name ('{row.AccountNumber}', '{row.Name}')");
                continue;
            }

            var accountClass = AccountClassMapper.FromAccountNumber(row.AccountNumber);
            if (accountClass is null)
            {
                skipped++;
                errors.Add($"Skipped '{row.AccountNumber}': cannot determine account class");
                continue;
            }

            if (existingAccounts.TryGetValue(row.AccountNumber, out var existing))
            {
                existing.Name = row.Name;
                existing.AccountClass = accountClass.Value;
                updated++;
            }
            else
            {
                var account = new Account
                {
                    AccountNumber = row.AccountNumber,
                    Name = row.Name,
                    AccountClass = accountClass.Value,
                    IsActive = true
                };
                _db.Accounts.Add(account);
                existingAccounts[row.AccountNumber] = account;
                created++;
            }
        }

        await _db.SaveChangesAsync();
        return new CsvImportResult(created, updated, skipped, errors);
    }

    private sealed class AccountCsvRow
    {
        public string AccountNumber { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
