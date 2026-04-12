using jsiSIE;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace KoalaBooks.Infrastructure.Services;

public record SieImportPreview(
    string? CompanyName,
    string? OrgNumber,
    int SieType,
    List<SieImportFiscalYear> FiscalYears,
    int AccountCount,
    int VoucherCount);

public record SieImportFiscalYear(
    int RarId,
    DateOnly Start,
    DateOnly End,
    string Label,
    int VoucherCount,
    bool ExistsInDatabase,
    int? ExistingFiscalYearId);

public record SieImportResult(
    int AccountsCreated,
    int AccountsUpdated,
    int EntriesImported,
    int LinesImported,
    string FiscalYearName,
    List<string> Warnings);

public class SieImportService
{
    private readonly AppDbContext _db;

    public SieImportService(AppDbContext db)
    {
        _db = db;
    }

    public SieDocument Parse(Stream stream)
    {
        var doc = new SieDocument();
        doc.ReadDocument(stream);
        return doc;
    }

    public async Task<SieImportPreview> GetPreviewAsync(SieDocument doc)
    {
        var fiscalYears = new List<SieImportFiscalYear>();

        foreach (var kvp in doc.RAR.OrderBy(k => k.Key))
        {
            var rar = kvp.Value;
            if (rar.Start is null || rar.End is null) continue;

            var start = DateOnly.FromDateTime(rar.Start.Value);
            var end = DateOnly.FromDateTime(rar.End.Value);
            var label = $"{start.Year}" + (start.Year != end.Year ? $"/{end.Year}" : "");

            // Count vouchers that fall within this fiscal year
            var voucherCount = doc.VER.Count(v =>
            {
                var vDate = DateOnly.FromDateTime(v.VoucherDate);
                return vDate >= start && vDate <= end;
            });

            // Check if fiscal year exists in DB
            var existing = await _db.FiscalYears
                .FirstOrDefaultAsync(f => f.StartDate == start && f.EndDate == end);

            fiscalYears.Add(new SieImportFiscalYear(
                RarId: kvp.Key,
                Start: start,
                End: end,
                Label: label,
                VoucherCount: voucherCount,
                ExistsInDatabase: existing is not null,
                ExistingFiscalYearId: existing?.Id));
        }

        return new SieImportPreview(
            CompanyName: doc.FNAMN?.Name,
            OrgNumber: doc.FNAMN?.OrgIdentifier,
            SieType: doc.SIETYP,
            FiscalYears: fiscalYears,
            AccountCount: doc.KONTO.Count,
            VoucherCount: doc.VER.Count);
    }

    public async Task<SieImportResult> ImportFiscalYearAsync(
        SieDocument doc, int rarId, bool overwrite)
    {
        var warnings = new List<string>();

        if (!doc.RAR.TryGetValue(rarId, out var rar) || rar.Start is null || rar.End is null)
            throw new InvalidOperationException($"Fiscal year with RAR index {rarId} not found in SIE file.");

        var fyStart = DateOnly.FromDateTime(rar.Start.Value);
        var fyEnd = DateOnly.FromDateTime(rar.End.Value);
        var fyName = $"{fyStart.Year}" + (fyStart.Year != fyEnd.Year ? $"/{fyEnd.Year}" : "");

        // 1. Find or create fiscal year (must happen before account upsert)
        var fiscalYear = await _db.FiscalYears
            .FirstOrDefaultAsync(f => f.StartDate == fyStart && f.EndDate == fyEnd);

        if (fiscalYear is not null && overwrite)
        {
            // Delete existing journal entries and accounts for this fiscal year
            var existingEntries = await _db.JournalEntries
                .Include(j => j.Lines)
                .Where(j => j.FiscalYearId == fiscalYear.Id)
                .ToListAsync();
            _db.JournalEntries.RemoveRange(existingEntries);

            var existingAccounts = await _db.Accounts
                .Where(a => a.FiscalYearId == fiscalYear.Id)
                .ToListAsync();
            _db.Accounts.RemoveRange(existingAccounts);

            await _db.SaveChangesAsync();
            fiscalYear.IsClosed = false;
        }
        else if (fiscalYear is not null && !overwrite)
        {
            throw new InvalidOperationException(
                $"Fiscal year {fyName} already exists. Set overwrite=true to replace.");
        }
        else
        {
            fiscalYear = new FiscalYear
            {
                Name = fyName,
                StartDate = fyStart,
                EndDate = fyEnd,
                IsClosed = false
            };
            _db.FiscalYears.Add(fiscalYear);
            await _db.SaveChangesAsync();
        }

        // 2. Upsert accounts scoped to this fiscal year
        var (accountsCreated, accountsUpdated) = await UpsertAccountsAsync(doc, fiscalYear.Id, warnings);

        // 3. Import vouchers for this fiscal year
        var accountLookup = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYear.Id)
            .ToDictionaryAsync(a => a.AccountNumber);
        var vouchers = doc.VER.Where(v =>
        {
            var vDate = DateOnly.FromDateTime(v.VoucherDate);
            return vDate >= fyStart && vDate <= fyEnd;
        }).OrderBy(v => v.VoucherDate).ThenBy(v => v.Number).ToList();

        int entriesImported = 0;
        int linesImported = 0;
        int entryNumber = 0;

        foreach (var voucher in vouchers)
        {
            entryNumber++;
            var entry = new JournalEntry
            {
                EntryNumber = entryNumber,
                Date = DateOnly.FromDateTime(voucher.VoucherDate),
                Description = BuildDescription(voucher),
                FiscalYearId = fiscalYear.Id,
                CreatedAt = DateTime.UtcNow,
                Lines = []
            };

            foreach (var row in voucher.Rows)
            {
                if (row.Account is null)
                {
                    warnings.Add($"Voucher {voucher.Series}{voucher.Number}: skipped row with null account.");
                    continue;
                }

                if (!accountLookup.TryGetValue(row.Account.Number, out var account))
                {
                    warnings.Add($"Voucher {voucher.Series}{voucher.Number}: account {row.Account.Number} not found, skipped.");
                    continue;
                }

                var line = new JournalEntryLine
                {
                    AccountId = account.Id,
                    DebitAmount = row.Amount >= 0 ? row.Amount : 0,
                    CreditAmount = row.Amount < 0 ? Math.Abs(row.Amount) : 0
                };
                entry.Lines.Add(line);
                linesImported++;
            }

            if (entry.Lines.Count >= 2)
            {
                _db.JournalEntries.Add(entry);
                entriesImported++;
            }
            else if (entry.Lines.Count > 0)
            {
                warnings.Add($"Voucher {voucher.Series}{voucher.Number}: only {entry.Lines.Count} line(s), skipped (need at least 2).");
            }
        }

        await _db.SaveChangesAsync();

        return new SieImportResult(
            AccountsCreated: accountsCreated,
            AccountsUpdated: accountsUpdated,
            EntriesImported: entriesImported,
            LinesImported: linesImported,
            FiscalYearName: fyName,
            Warnings: warnings);
    }

    private async Task<(int Created, int Updated)> UpsertAccountsAsync(
        SieDocument doc, int fiscalYearId, List<string> warnings)
    {
        var existingAccounts = await _db.Accounts
            .Where(a => a.FiscalYearId == fiscalYearId)
            .ToDictionaryAsync(a => a.AccountNumber);
        int created = 0, updated = 0;

        foreach (var kvp in doc.KONTO)
        {
            var sieAccount = kvp.Value;
            var accountClass = AccountClassMapper.FromAccountNumber(sieAccount.Number);
            if (accountClass is null)
            {
                warnings.Add($"Account {sieAccount.Number}: cannot determine class, skipped.");
                continue;
            }

            if (existingAccounts.TryGetValue(sieAccount.Number, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(sieAccount.Name))
                {
                    existing.Name = sieAccount.Name;
                    existing.AccountClass = accountClass.Value;
                    updated++;
                }
            }
            else
            {
                var account = new Account
                {
                    AccountNumber = sieAccount.Number,
                    Name = string.IsNullOrWhiteSpace(sieAccount.Name) ? sieAccount.Number : sieAccount.Name,
                    AccountClass = accountClass.Value,
                    IsActive = true,
                    FiscalYearId = fiscalYearId
                };
                _db.Accounts.Add(account);
                existingAccounts[sieAccount.Number] = account;
                created++;
            }
        }

        await _db.SaveChangesAsync();
        return (created, updated);
    }

    private static string BuildDescription(SieVoucher voucher)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(voucher.Series))
            parts.Add($"[{voucher.Series}]");
        if (!string.IsNullOrWhiteSpace(voucher.Number))
            parts.Add($"#{voucher.Number}");
        if (!string.IsNullOrWhiteSpace(voucher.Text))
            parts.Add(voucher.Text);

        return parts.Count > 0 ? string.Join(" ", parts) : "Imported from SIE";
    }
}
